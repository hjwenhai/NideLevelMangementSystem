package command

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"levelgateway/protocols"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"levelgateway/src/internal/platform"
	"levelgateway/src/internal/storage"
	"sync"
	"time"
)

// V3.0-D2 external states; the retired mailbox states are different.
const (
	Idle      = 0
	Accepted  = 1
	Running   = 2
	Confirmed = 3
	Failed    = 4
	Uncertain = 5
)

type Request struct {
	ID        string `json:"id"`
	Slot      int    `json:"slot"`
	Binding   uint32 `json:"binding_version"`
	Revision  uint32 `json:"config_revision"`
	Port      int    `json:"port"`
	Unit      byte   `json:"unit"`
	TimeoutMS int    `json:"timeout_ms"`
	Command   uint16 `json:"command"`
	Action    uint16 `json:"south_action"`
	Stop      bool   `json:"stop"`
	Source    string `json:"source"`
	TargetRaw int32  `json:"level_target_raw,omitempty"`
	Operator  string `json:"operator,omitempty"`
	Reason    string `json:"reason,omitempty"`
}
type Cycle struct {
	Value           uint16           `json:"value"`
	State           int              `json:"state"`
	Error           uint16           `json:"error"`
	Count           uint32           `json:"count"`
	Request         Request          `json:"request"`
	Sent            bool             `json:"sent"`
	AcceptedMS      int64            `json:"accepted_ms"`
	UpdatedMS       int64            `json:"updated_ms"`
	Message         string           `json:"message,omitempty"`
	Readback        []uint16         `json:"readback,omitempty"`
	Level           *LevelAdjustment `json:"level_adjustment,omitempty"`
	CancelRequested bool             `json:"cancel_requested,omitempty"`
}
type Slot struct {
	Number int   `json:"slot"`
	Main   Cycle `json:"main"`
	Stop   Cycle `json:"stop"`
}
type Engine struct {
	mu       sync.Mutex
	Store    *storage.Store
	slots    [config.MaxMeters]Slot
	queue    []Request
	targets  [config.MaxMeters]levelTarget
	failure  string
	reliable bool
	freeze   func(int, uint16, bool, string) (Request, error)
	validate func(Request) error
}

func New(s *storage.Store, freeze func(int, uint16, bool, string) (Request, error), validate func(Request) error) *Engine {
	e := &Engine{Store: s, freeze: freeze, validate: validate, reliable: true}
	for i := range e.slots {
		e.slots[i].Number = i + 1
	}
	return e
}
func Action(cmd uint16) (uint16, bool) {
	if cmd == LevelAdjustCommand {
		return 0, true
	}
	return protocols.SouthAction(cmd)
}
func channel(s *Slot, stop bool) *Cycle {
	if stop {
		return &s.Stop
	}
	return &s.Main
}
func cloneSlot(s Slot) Slot {
	if s.Main.Level != nil {
		v := *s.Main.Level
		s.Main.Level = &v
	}
	s.Main.Readback = append([]uint16(nil), s.Main.Readback...)
	s.Stop.Readback = append([]uint16(nil), s.Stop.Readback...)
	return s
}
func (e *Engine) save(s Slot, kind, msg string) error {
	b, err := json.Marshal(map[string]any{"message": msg, "snapshot": s})
	if err != nil {
		return err
	}
	return e.Store.SaveControls([]storage.ControlChange{{Slot: s.Number, Data: s}}, kind, string(b))
}

// Reset is a local, durable state change. It never queues or sends an action.
// Modbus carries no human identity/confirmation: the client must obtain and log it.
func (e *Engine) saveReset(before, after Slot, stop bool, kind, source, operator, reason string) error {
	confirmationRecord := "upstream-client"
	if kind == "control_maintenance_reset" {
		confirmationRecord = "gateway-web"
	}
	b, err := json.Marshal(map[string]any{
		"source": source, "reset_time_ms": time.Now().UnixMilli(), "stop": stop,
		"before": before, "after": after, "operator": operator, "reason": reason,
		"confirmation_record": confirmationRecord, "manual_confirmation_required": channel(&before, stop).State == Uncertain,
	})
	if err != nil {
		return err
	}
	// A stop reset must never resolve the independent legacy main cycle.
	resolve := !stop && before.Main.Request.Source == "legacy-v2"
	return e.Store.SaveControls([]storage.ControlChange{{Slot: after.Number, Data: after, ResolveLegacy: resolve}}, kind, string(b))
}

func resetCycle(c *Cycle) {
	marker := c.Request.Source
	*c = Cycle{Count: c.Count}
	if marker == "legacy-v2" || marker == "migration-resolved" {
		c.Request.Source = "migration-resolved"
	}
}
func (e *Engine) restoreFailure(err error) error {
	e.failure = err.Error()
	e.reliable = false
	return err
}
func (e *Engine) Recover() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	raw, err := e.Store.LoadControls()
	if err != nil {
		return e.restoreFailure(err)
	}
	for n, b := range raw {
		var s Slot
		if n < 1 || n > config.MaxMeters {
			return e.restoreFailure(fmt.Errorf("invalid slot %d", n))
		}
		if err = json.Unmarshal(b, &s); err != nil || s.Number != n {
			return e.restoreFailure(fmt.Errorf("control restore slot%d: %v", n, err))
		}
		for i, c := range []*Cycle{&s.Main, &s.Stop} {
			if !validCycle(*c, n, i == 1) {
				return e.restoreFailure(fmt.Errorf("corrupt control slot%d", n))
			}
		}
		changed := false
		for _, c := range []*Cycle{&s.Main, &s.Stop} {
			if c.State == Accepted || c.State == Running {
				c.State = Uncertain
				c.Error = 9
				c.Message = "运行中断，未决动作不重放"
				c.UpdatedMS = time.Now().UnixMilli()
				changed = true
			}
		}
		if changed {
			if err = e.save(s, "control_recovery", fmt.Sprintf("槽位%d未决周期转5/9，不重放", n)); err != nil {
				return e.restoreFailure(err)
			}
		}
		e.slots[n-1] = s
	}
	legacy, err := e.Store.LegacyUnresolved()
	if err != nil {
		return e.restoreFailure(err)
	}
	for _, b := range legacy {
		var old struct {
			Request struct {
				Slot    int      `json:"slot"`
				Binding uint32   `json:"binding_version"`
				Values  []uint16 `json:"values"`
			}
			State int
		}
		if err = json.Unmarshal(b, &old); err != nil || old.Request.Slot < 1 || old.Request.Slot > config.MaxMeters || old.Request.Binding == 0 {
			return e.restoreFailure(fmt.Errorf("旧V2未决记录无效: %v", err))
		}
		n := old.Request.Slot
		s := e.slots[n-1]
		if s.Main.State != 0 {
			continue
		}
		cmd := uint16(4)
		if len(old.Request.Values) == 1 {
			for _, v := range []uint16{1, 2, 3, 4, 5, 6, 7, 9, 10} {
				a, _ := Action(v)
				if a == old.Request.Values[0] {
					cmd = v
				}
			}
		}
		s.Main = Cycle{Value: cmd, State: Uncertain, Error: 9, Request: Request{ID: fmt.Sprintf("legacy-v2-slot-%d", n), Slot: n, Binding: old.Request.Binding, Command: cmd, Source: "legacy-v2"}, Message: "V2遗留未决/不确定动作，须人工核查；Port/Unit=0表示旧记录未冻结此信息，原始目标见commands审计；绝不下发"}
		if err = e.save(s, "control_migration", s.Main.Message); err != nil {
			return e.restoreFailure(err)
		}
		e.slots[n-1] = s
	}
	return nil
}
func validCycle(c Cycle, n int, stop bool) bool {
	if c.Request.Command == LevelAdjustCommand && (!ValidLevelTarget(c.Request.TargetRaw) || stop) {
		return false
	}
	if c.State < 0 || c.State > 5 || c.Error > 14 {
		return false
	}
	if c.State == Idle {
		return c.Value == 0 && c.Error == 0 && !c.Sent
	}
	if c.Request.ID == "" || c.Request.Slot != n || c.Request.Binding == 0 {
		return false
	}
	if c.Request.Source == "legacy-v2" {
		return !stop && c.State == Uncertain && c.Error == 9 && !c.Sent
	}
	a, ok := Action(c.Request.Command)
	if !ok || a != c.Request.Action || c.Request.Stop != stop || c.Request.Revision == 0 || c.Request.Port < 1 || c.Request.Port > 4 || c.Request.Unit < 1 || c.Request.Unit > 243 || c.Request.TimeoutMS < 50 || c.Request.TimeoutMS > 5000 || c.Count == 0 {
		return false
	}
	if stop {
		if c.Value != 1 || c.Request.Command != 4 {
			return false
		}
	} else if c.Value != c.Request.Command {
		return false
	}
	if c.State <= Confirmed && c.Error != 0 || c.State >= Failed && c.Error == 0 || c.State == Confirmed && !c.Sent {
		return false
	}
	return true
}
func (e *Engine) Status() string { e.mu.Lock(); defer e.mu.Unlock(); return e.failure }
func (e *Engine) Results() []Slot {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := make([]Slot, config.MaxMeters)
	for i, s := range e.slots {
		out[i] = cloneSlot(s)
	}
	return out
}
func (e *Engine) HasCycles() bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.failure != "" {
		return true
	}
	for _, s := range e.slots {
		if s.Main.State != 0 || s.Stop.State != 0 {
			return true
		}
	}
	return false
}
func (e *Engine) Words() ([]uint16, error) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if !e.reliable {
		return nil, modbus.DeviceFailure
	}
	w := make([]uint16, protocols.SimpleControlStride*config.MaxMeters)
	for i, s := range e.slots {
		b := i * 12
		w[b] = s.Main.Value
		w[b+1] = uint16(s.Main.State)
		w[b+2] = s.Main.Error
		modbus.Put32(w[b+4:], s.Main.Count)
		w[b+6] = s.Main.Value
		w[b+7] = s.Stop.Value
		w[b+8] = uint16(s.Stop.State)
		w[b+9] = s.Stop.Error
		modbus.Put32(w[b+10:], s.Stop.Count)
	}
	return w, nil
}
func (e *Engine) Submit(n int, stop bool, value uint16, source string) error {
	if n < 1 || n > config.MaxMeters {
		return modbus.IllegalAddress
	}
	if stop && value > 1 {
		return modbus.IllegalValue
	}
	if !stop && value != 0 {
		if _, ok := Action(value); !ok {
			return modbus.IllegalValue
		}
	}
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.submitLocked(n, stop, value, source)
}
func (e *Engine) submitLocked(n int, stop bool, value uint16, source string) error {
	if e.failure != "" {
		return modbus.DeviceFailure
	}
	old := e.slots[n-1]
	s := cloneSlot(old)
	c := channel(&s, stop)
	if value == 0 {
		if c.State == Accepted || c.State == Running {
			return modbus.Busy
		}
		if c.State == Idle {
			return nil
		}
		resetCycle(c)
		if err := e.saveReset(old, s, stop, "control_reset", source, "", ""); err != nil {
			return modbus.DeviceFailure
		}
		e.slots[n-1] = s
		return nil
	}
	if c.State != Idle {
		if c.Value == value {
			return nil
		}
		return modbus.Busy
	}
	if !stop && s.Stop.State != Idle {
		return modbus.Busy
	}
	cmd := value
	if stop {
		cmd = 4
	}
	r, err := e.freeze(n, cmd, stop, source)
	if err != nil {
		return err
	}
	if cmd == LevelAdjustCommand {
		t := e.targets[n-1]
		if !t.ready || t.binding != r.Binding || t.revision != r.Revision {
			return modbus.IllegalValue
		}
		r.TargetRaw = t.raw
		r.Operator = t.operator
		r.Reason = t.reason
	}
	if stop && s.Main.State != Idle {
		old := s.Main.Request
		if old.Binding != r.Binding || old.Port > 0 && (old.Port != r.Port || old.Unit != r.Unit) {
			return modbus.IllegalAddress
		}
	}
	id := make([]byte, 16)
	if _, err = rand.Read(id); err != nil {
		return modbus.DeviceFailure
	}
	r.ID = hex.EncodeToString(id)
	now := time.Now().UnixMilli()
	count := c.Count
	if count < ^uint32(0) {
		count++
	}
	*c = Cycle{Value: value, State: Accepted, Count: count, Request: r, AcceptedMS: now, UpdatedMS: now}
	if stop && (s.Main.State == Accepted || s.Main.State == Running) && !s.Main.Sent {
		if s.Main.Request.Command == LevelAdjustCommand && s.Main.State == Running {
			s.Main.CancelRequested = true
		} else {
			s.Main.State = Failed
			s.Main.Error = 11
		}
		s.Main.Message = "独立停止取消尚未发送正常项"
		s.Main.UpdatedMS = now
	}
	if err = e.save(s, "control_accept", fmt.Sprintf("%s 槽位%d 绑定%d ID=%s 指令%d stop=%t", source, n, r.Binding, r.ID, cmd, stop)); err != nil {
		return modbus.DeviceFailure
	}
	e.slots[n-1] = s
	e.targets[n-1].ready = false
	e.queue = append(e.queue, r)
	return nil
}
func (e *Engine) Pending(port int) int {
	e.mu.Lock()
	defer e.mu.Unlock()
	n := 0
	for _, r := range e.queue {
		c := channel(&e.slots[r.Slot-1], r.Stop)
		if r.Port == port && c.Request.ID == r.ID && c.State == Accepted {
			n++
		}
	}
	return n
}
func (e *Engine) Pop(port int, stopOnly bool) (Request, bool) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.failure != "" {
		return Request{}, false
	}
	// Drop canceled queue entries so repeated accepted/reset cycles cannot grow memory.
	live := e.queue[:0]
	for _, r := range e.queue {
		c := channel(&e.slots[r.Slot-1], r.Stop)
		if c.Request.ID == r.ID && c.State == Accepted {
			live = append(live, r)
		}
	}
	e.queue = live
	idx := -1
	for i, r := range e.queue {
		if r.Port == port && (!stopOnly || r.Stop) {
			if idx < 0 || r.Stop {
				idx = i
				if r.Stop {
					break
				}
			}
		}
	}
	if idx < 0 {
		return Request{}, false
	}
	r := e.queue[idx]
	s := cloneSlot(e.slots[r.Slot-1])
	c := channel(&s, r.Stop)
	c.State = Running
	c.UpdatedMS = time.Now().UnixMilli()
	if err := e.save(s, "control_running", r.ID); err != nil {
		e.latchFailure(r, err)
		return Request{}, false
	}
	e.slots[r.Slot-1] = s
	e.queue = append(e.queue[:idx], e.queue[idx+1:]...)
	return r, true
}
func (e *Engine) latchFailure(r Request, err error) {
	e.failure = err.Error()
	c := channel(&e.slots[r.Slot-1], r.Stop)
	c.State = Uncertain
	c.Error = 8
	c.Message = "受理后持久化失败: " + err.Error()
}
func (e *Engine) update(r Request, state int, code uint16, msg string, back []uint16) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	c := channel(&e.slots[r.Slot-1], r.Stop)
	if e.failure != "" || c.Request.ID != r.ID || c.State != Running {
		return false
	}
	s := cloneSlot(e.slots[r.Slot-1])
	c = channel(&s, r.Stop)
	c.State = state
	c.Error = code
	c.Message = msg
	c.Readback = append([]uint16(nil), back...)
	c.UpdatedMS = time.Now().UnixMilli()
	if err := e.save(s, "control_result", fmt.Sprintf("ID=%s state=%d error=%d %s", r.ID, state, code, msg)); err != nil {
		e.latchFailure(r, err)
		return false
	}
	e.slots[r.Slot-1] = s
	return true
}

// Last durable gate before HR699. Stop may cancel during preflight/auth; after
// this claim an action is conservatively possibly sent and cannot be canceled.
func (e *Engine) claimSend(r Request) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	c := channel(&e.slots[r.Slot-1], r.Stop)
	if e.failure != "" || c.Request.ID != r.ID || c.State != Running || c.Sent || c.CancelRequested {
		return false
	}
	s := cloneSlot(e.slots[r.Slot-1])
	channel(&s, r.Stop).Sent = true
	if err := e.save(s, "control_send_claim", r.ID); err != nil {
		e.latchFailure(r, err)
		return false
	}
	e.slots[r.Slot-1] = s
	return true
}
func (e *Engine) Execute(ctx context.Context, r Request, w platform.Wire) {
	if err := e.validate(r); err != nil {
		e.update(r, Failed, 1, "绑定/配置或条件已改变，未发送", nil)
		return
	}
	if r.Command == LevelAdjustCommand {
		e.executeLevel(ctx, r, w)
		return
	}
	read := func(fc byte, a, n uint16) ([]uint16, error) {
		if !e.active(r) {
			return nil, context.Canceled
		}
		cc, cancel := context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
		defer cancel()
		return platform.ReadRegisters(cc, w, r.Unit, fc, a, n)
	}
	write := func(a, v uint16) error {
		if !e.active(r) {
			return context.Canceled
		}
		cc, cancel := context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
		defer cancel()
		return platform.WriteRegisters(cc, w, r.Unit, a, []uint16{v})
	}
	states, err := read(4, 51, 2)
	if err != nil {
		e.update(r, Failed, 10, "无法取得新鲜仪表状态，未发送: "+err.Error(), nil)
		return
	}
	// Keep fresh mode/fault diagnostics; the instrument decides whether an action is allowed.
	before, _ := read(3, 698, 1)
	if err = write(129, 0x10d9); err != nil {
		e.update(r, Failed, 4, "认证未确认，未发送控制: "+err.Error(), nil)
		return
	}
	if ctx.Err() != nil {
		e.update(r, Failed, 10, "通道中断，未发送控制", nil)
		return
	}
	if !e.claimSend(r) {
		return
	}
	writeErr := write(699, r.Action)
	var ex modbus.Exception
	if errors.As(writeErr, &ex) {
		e.update(r, Failed, 5, "仪表明确拒绝控制: "+writeErr.Error(), nil)
		return
	}
	// Never retry HR699, even when its response is lost.
	back, readErr := read(3, 698, 1)
	states, stateErr := read(4, 51, 2)
	observable := writeErr == nil || len(before) == 1 && before[0]&r.Action == 0
	if observable && readErr == nil && stateErr == nil && len(back) == 1 && len(states) == 2 && back[0]&r.Action != 0 {
		e.update(r, Confirmed, 0, "指令回读已确认，不代表机械完成", append(back, states...))
		return
	}
	code := uint16(7)
	msg := "回读不匹配或失败"
	if writeErr != nil {
		code = 6
		msg = "控制应答未确认: " + writeErr.Error()
	}
	if ctx.Err() != nil {
		code = 9
		msg = "执行期间运行中断，需核查"
	}
	e.update(r, Uncertain, code, msg, append(back, states...))
}
func (e *Engine) active(r Request) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	c := channel(&e.slots[r.Slot-1], r.Stop)
	return e.failure == "" && c.State == Running && !c.CancelRequested && c.Request.ID == r.ID
}
func (e *Engine) InterruptPort(port int) {
	e.mu.Lock()
	defer e.mu.Unlock()
	for i, old := range e.slots {
		s := cloneSlot(old)
		changed := false
		for _, c := range []*Cycle{&s.Main, &s.Stop} {
			if c.Request.Port == port && (c.State == Accepted || c.State == Running) {
				c.State = Uncertain
				c.Error = 9
				c.Message = "通道停止，未决动作不重放"
				changed = true
			}
		}
		if changed {
			if err := e.save(s, "control_interrupt", fmt.Sprintf("COM%d 槽位%d", port, i+1)); err != nil {
				e.failure = err.Error()
				e.reliable = false
				return
			}
			e.slots[i] = s
		}
	}
}
func (e *Engine) MaintenanceReset(n int, stop bool, id, operator, reason string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if n < 1 || n > config.MaxMeters {
		return modbus.IllegalAddress
	}
	if e.failure != "" {
		return modbus.DeviceFailure
	}
	old := e.slots[n-1]
	c := channel(&old, stop)
	if (c.State != Failed && c.State != Uncertain && !(c.State == Confirmed && c.Request.Command == LevelAdjustCommand)) || id == "" || c.Request.ID != id || len(operator) < 1 || len(operator) > 128 || len(reason) < 4 || len(reason) > 1024 {
		return modbus.IllegalValue
	}
	s := cloneSlot(old)
	resetCycle(channel(&s, stop))
	if err := e.saveReset(old, s, stop, "control_maintenance_reset", "web/manual-review", operator, reason); err != nil {
		return modbus.DeviceFailure
	}
	e.slots[n-1] = s
	return nil
}
