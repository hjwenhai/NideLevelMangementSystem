package command

import (
	"context"
	"errors"
	"fmt"
	"levelgateway/protocols"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"levelgateway/src/internal/platform"
	"time"
)

const LevelAdjustCommand uint16 = 11

// A gateway permission token only. This is NEVER written to FW HR699.
const LevelAdjustPermission uint16 = 512
const levelMin int32 = -99999
const levelMax int32 = 999999

type levelTarget struct {
	raw               int32
	ready             bool
	binding, revision uint32
	request           Request
	operator, reason  string
}
type LevelAdjustment struct {
	CurrentRaw        int32 `json:"fresh_level_raw"`
	PreviousOffsetRaw int32 `json:"previous_offset_raw"`
	NewOffsetRaw      int32 `json:"new_offset_raw"`
	PasswordCleared   bool  `json:"password_cleared"`
}

func ValidLevelTarget(raw int32) bool { return raw >= levelMin && raw <= levelMax }
func (e *Engine) StageLevel(n int, raw int32, source string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.stageLevel(n, raw, source, "", "")
}
func (e *Engine) stageLevel(n int, raw int32, source, operator, reason string) error {
	if n < 1 || n > config.MaxMeters {
		return modbus.IllegalAddress
	}
	if !ValidLevelTarget(raw) {
		return modbus.IllegalValue
	}
	if e.failure != "" {
		return modbus.DeviceFailure
	}
	s := e.slots[n-1]
	if s.Main.State != Idle || s.Stop.State != Idle {
		return modbus.Busy
	}
	r, err := e.freeze(n, LevelAdjustCommand, false, source)
	if err != nil {
		return err
	}
	e.targets[n-1] = levelTarget{raw: raw, ready: true, binding: r.Binding, revision: r.Revision, request: r, operator: operator, reason: reason}
	return nil
}

// A web submission freezes its own target and submits atomically under the same lock.
func (e *Engine) SubmitLevel(n int, raw int32, source, operator, reason string) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if err := e.stageLevel(n, raw, source, operator, reason); err != nil {
		return err
	}
	return e.submitLocked(n, false, LevelAdjustCommand, source)
}
func (e *Engine) LevelWords() []uint16 {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := make([]uint16, config.MaxMeters*protocols.LevelTargetStride)
	for i, t := range e.targets {
		s := e.slots[i]
		raw := t.raw
		ready := t.ready
		if s.Main.State != Idle && s.Main.Request.Command == LevelAdjustCommand {
			raw = s.Main.Request.TargetRaw
			ready = false
		}
		modbus.Put32(out[i*4:], uint32(raw))
		if ready {
			if e.validate(t.request) == nil {
				out[i*4+2] = 1
			}
		}
	}
	return out
}

// Persist calculation evidence before any offset write. It never changes a canceled cycle.
func (e *Engine) levelEvidence(r Request, v LevelAdjustment) bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.failure != "" {
		return false
	}
	old := e.slots[r.Slot-1]
	if old.Main.State != Running || old.Main.Request.ID != r.ID {
		return false
	}
	s := cloneSlot(old)
	s.Main.Level = &v
	if err := e.save(s, "level_adjust_evidence", r.ID); err != nil {
		e.latchFailure(r, err)
		return false
	}
	e.slots[r.Slot-1] = s
	return true
}
func (e *Engine) executeLevel(ctx context.Context, r Request, w platform.Wire) {
	read := func(fc byte, a, n uint16) ([]uint16, error) {
		if !e.active(r) {
			return nil, context.Canceled
		}
		cc, cancel := context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
		defer cancel()
		return platform.ReadRegisters(cc, w, r.Unit, fc, a, n)
	}
	current, err := read(4, 0, 2)
	if err != nil {
		e.update(r, Failed, 10, "无法取得新鲜液位，未修改: "+err.Error(), nil)
		return
	}
	offset, err := read(3, 503, 2)
	if err != nil {
		e.update(r, Failed, 10, "无法读取HR503/504偏移量（请确认仪表支持），未修改: "+err.Error(), nil)
		return
	}
	signed := func(v []uint16) int32 { return int32(uint32(v[0])<<16 | uint32(v[1])) }
	evidence := LevelAdjustment{CurrentRaw: signed(current), PreviousOffsetRaw: signed(offset)}
	calculated := int64(r.TargetRaw) - int64(evidence.CurrentRaw) + int64(evidence.PreviousOffsetRaw)
	if !ValidLevelTarget(r.TargetRaw) || calculated < int64(levelMin) || calculated > int64(levelMax) {
		e.update(r, Failed, 12, "计算出的新偏移量超出-9999.9～99999.9mm，未修改", nil)
		return
	}
	evidence.NewOffsetRaw = int32(calculated)
	if !e.levelEvidence(r, evidence) || !e.active(r) {
		return
	}
	// Always attempt FC06 password clear after an authentication attempt, even if
	// the auth response was lost, the port was canceled, or stop canceled this job.
	auth := func(value uint16) error {
		cc, cancel := context.WithTimeout(context.Background(), time.Duration(r.TimeoutMS)*time.Millisecond)
		defer cancel()
		return platform.WriteSingleRegister(cc, w, r.Unit, 129, value)
	}
	cc, cancel := context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
	authErr := platform.WriteSingleRegister(cc, w, r.Unit, 129, 0xea66)
	cancel()
	state, code, msg := Failed, uint16(4), "远程液位认证未确认，未写偏移量"
	var back []uint16
	if authErr == nil && ctx.Err() == nil && e.claimSend(r) {
		values := make([]uint16, 2)
		modbus.Put32(values, uint32(evidence.NewOffsetRaw))
		cc, cancel = context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
		writeErr := platform.WriteRegisters(cc, w, r.Unit, 503, values)
		cancel()
		var ex modbus.Exception
		if errors.As(writeErr, &ex) {
			state, code, msg = Failed, 5, "仪表明确拒绝修改偏移量: "+writeErr.Error()
		} else {
			back, err = read(3, 503, 2)
			if err == nil && len(back) == 2 && signed(back) == evidence.NewOffsetRaw {
				state, code, msg = Confirmed, 0, "HR503/504回读一致，液位偏移修改已确认"
			} else {
				state, code, msg = Uncertain, 13, "偏移量回读不一致或失败，禁止自动重写"
				if writeErr != nil {
					code = 6
					msg = "偏移量写应答未确认，禁止自动重写: " + writeErr.Error()
				}
			}
		}
	} else if authErr != nil {
		msg += ": " + authErr.Error()
	}
	clearErr := auth(0)
	evidence.PasswordCleared = clearErr == nil
	if !e.levelEvidence(r, evidence) {
		return
	}
	if clearErr != nil {
		state, code, msg = Uncertain, 14, "HR129清零应答未确认，须人工核查认证及偏移量: "+clearErr.Error()
	}
	if clearErr == nil {
		e.mu.Lock()
		c := e.slots[r.Slot-1].Main
		if c.Request.ID == r.ID && c.CancelRequested {
			state, code, msg = Failed, 11, "独立停止取消尚未写入的液位修改；认证已清零"
		}
		e.mu.Unlock()
	}
	e.update(r, state, code, fmt.Sprintf("%s；目标%.1fmm，原液位%.1fmm，原偏移%.1fmm，新偏移%.1fmm", msg, float64(r.TargetRaw)/10, float64(evidence.CurrentRaw)/10, float64(evidence.PreviousOffsetRaw)/10, float64(evidence.NewOffsetRaw)/10), back)
}
