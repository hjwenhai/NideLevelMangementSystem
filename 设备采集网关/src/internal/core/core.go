package core

import (
	"context"
	"crypto/rand"
	"encoding/binary"
	"errors"
	"fmt"
	"levelgateway/protocols"
	"levelgateway/src/internal/cache"
	"levelgateway/src/internal/clockstate"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/licensing"
	"levelgateway/src/internal/modbus"
	"levelgateway/src/internal/network"
	"levelgateway/src/internal/platform"
	"levelgateway/src/internal/storage"
	"levelgateway/src/internal/update"
	"net"
	"reflect"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type Runtime struct {
	Port         config.Port
	Meters       []config.Meter
	Wire         platform.Wire
	Serial       *platform.Serial
	Simulator    *fw9000.Simulator
	cancel       context.CancelFunc
	done         chan struct{}
	mu           sync.Mutex
	closing      bool
	Error        string
	Transactions atomic.Uint64
	Failures     atomic.Uint64
	Progress     atomic.Int64
	CycleMS      atomic.Int64
}
type Manager struct {
	mu              sync.RWMutex
	applyMu         sync.Mutex
	ledMu           sync.RWMutex
	led             IndicatorStatus
	cfg             config.Config
	path            string
	ctx             context.Context
	cancel          context.CancelFunc
	Ports           map[int]*Runtime
	Cache           *cache.Cache
	Store           *storage.Store
	Engine          *command.Engine
	Boot            uint64
	Started         time.Time
	Simulate        bool
	HardwareWrites  bool
	TimeTrusted     atomic.Bool
	clockMu         sync.Mutex
	clock           *clockstate.Controller
	ClockGeneration atomic.Uint64
	ApplyingSinceMS atomic.Int64
	closing         atomic.Bool
	upgrade         *update.Service
	restart         restartState
	license         *licensing.Controller
	network         *network.Service
	networkLaunch   func(string) error
	tcp             net.Listener
	tcpCancel       context.CancelFunc
	tcpDone         chan struct{}
	wg              sync.WaitGroup
}

func New(parent context.Context, c config.Config, path string, s *storage.Store, simulate, hardwareWrites bool, clocks ...*clockstate.Controller) (*Manager, error) {
	return newManager(parent, c, path, s, simulate, hardwareWrites, nil, nil, nil, clocks...)
}
func NewWithUpgrade(parent context.Context, c config.Config, path string, s *storage.Store, simulate, hardwareWrites bool, u *update.Service, clocks ...*clockstate.Controller) (*Manager, error) {
	return newManager(parent, c, path, s, simulate, hardwareWrites, u, nil, nil, clocks...)
}
func NewWithServices(parent context.Context, c config.Config, path string, s *storage.Store, simulate, hardwareWrites bool, u *update.Service, n *network.Service, clocks ...*clockstate.Controller) (*Manager, error) {
	return newManager(parent, c, path, s, simulate, hardwareWrites, u, n, nil, clocks...)
}
func NewWithLicense(parent context.Context, c config.Config, path string, s *storage.Store, simulate, hardwareWrites bool, u *update.Service, n *network.Service, license *licensing.Controller, clocks ...*clockstate.Controller) (*Manager, error) {
	return newManager(parent, c, path, s, simulate, hardwareWrites, u, n, license, clocks...)
}
func newManager(parent context.Context, c config.Config, path string, s *storage.Store, simulate, hardwareWrites bool, upgrade *update.Service, networking *network.Service, license *licensing.Controller, clocks ...*clockstate.Controller) (*Manager, error) {
	if e := c.Validate(); e != nil {
		return nil, e
	}
	b := make([]byte, 8)
	if _, e := rand.Read(b); e != nil {
		return nil, e
	}
	ctx, cancel := context.WithCancel(parent)
	if upgrade != nil {
		_ = upgrade.ReconcileBoot()
	}
	m := &Manager{license: license, network: networking, upgrade: upgrade, cfg: c.Clone(), path: path, ctx: ctx, cancel: cancel, Ports: map[int]*Runtime{}, Cache: cache.New(), Store: s, Boot: binary.BigEndian.Uint64(b), Started: time.Now(), Simulate: simulate, HardwareWrites: hardwareWrites}
	m.clock = clockstate.New("", "/dev/rtc0", simulate, clockstate.Ops{Now: time.Now, Sleep: time.Sleep, SetSystem: platform.SetClock, ReadRTC: platform.ReadRTC, WriteRTC: platform.WriteRTC})
	if len(clocks) > 0 {
		m.clock = clocks[0]
	}
	m.TimeTrusted.Store(m.clock.Status.Trusted)
	m.Engine = command.New(s, m.freezeCommand, m.validateCommand)
	if e := m.Engine.Recover(); e != nil {
		// Keep measurement service running; control restoration fails closed.
		_ = s.Event("control_restore_failed", e.Error())
	}
	for _, meter := range c.Meters {
		m.Cache.Bind(meter)
	}
	for _, p := range c.Ports {
		rt, e := m.open(p, c)
		if e != nil {
			m.Close()
			return nil, fmt.Errorf("COM%d: %w", p.ID, e)
		}
		m.Ports[p.ID] = rt
	}
	if e := m.startTCP(c); e != nil {
		m.Close()
		return nil, e
	}
	for _, rt := range m.Ports {
		m.start(rt, c)
	}
	m.wg.Add(1)
	go m.history()
	return m, nil
}
func (m *Manager) Config() config.Config { m.mu.RLock(); defer m.mu.RUnlock(); return m.cfg.Clone() }
func (m *Manager) open(p config.Port, c config.Config) (*Runtime, error) {
	rt := &Runtime{Port: p, done: make(chan struct{})}
	if !m.license.Allowed() {
		rt.Port.Role = config.Disabled
		rt.Error = "设备永久授权未生效"
		return rt, nil
	}
	for _, meter := range c.Meters {
		if meter.Port == p.ID && meter.Enabled {
			rt.Meters = append(rt.Meters, meter)
		}
	}
	if p.Role == config.Disabled {
		return rt, nil
	}
	if m.Simulate {
		rt.Simulator = fw9000.NewSimulator()
		rt.Wire = rt.Simulator
	} else {
		s, e := platform.Open(p)
		if e != nil {
			return nil, e
		}
		rt.Wire = s
		rt.Serial = s
	}
	return rt, nil
}
func (m *Manager) start(rt *Runtime, c config.Config) {
	ctx, cancel := context.WithCancel(m.ctx)
	rt.cancel = cancel
	rt.Progress.Store(time.Since(m.Started).Milliseconds())
	go func() {
		defer close(rt.done)
		if rt.Port.Role == config.South && c.Mode != "maintenance" {
			m.poll(ctx, rt, c)
		} else if rt.Port.Role == config.North && rt.Serial != nil {
			if e := rt.Serial.Serve(ctx, m.Handle); e != nil {
				rt.mu.Lock()
				rt.Error = e.Error()
				rt.mu.Unlock()
			}
		} else {
			<-ctx.Done()
		}
	}()
}
func (m *Manager) stop(rt *Runtime) {
	if rt == nil {
		return
	}
	rt.mu.Lock()
	rt.closing = true
	rt.mu.Unlock()
	if rt.cancel != nil {
		rt.cancel()
		<-rt.done
	}
	m.Engine.InterruptPort(rt.Port.ID)
	if rt.Wire != nil {
		_ = rt.Wire.Close()
	}
}
func (m *Manager) startTCP(c config.Config) error {
	if !c.TCP.Enabled || !m.license.Allowed() {
		return nil
	}
	listen := c.TCP.Listen
	if m.network != nil && m.network.Opts.Fixture && m.Simulate {
		_, port, _ := net.SplitHostPort(listen)
		listen = net.JoinHostPort("127.0.0.1", port)
	}
	l, e := net.Listen("tcp", listen)
	if e != nil {
		return e
	}
	ctx, cancel := context.WithCancel(m.ctx)
	m.tcp = l
	m.tcpCancel = cancel
	m.tcpDone = make(chan struct{})
	go func() { defer close(m.tcpDone); _ = modbus.ServeTCP(ctx, l, c.TCP.MaxClients, m.Handle) }()
	return nil
}
func (m *Manager) stopTCP() {
	if m.tcpCancel != nil {
		m.tcpCancel()
		<-m.tcpDone
		m.tcpCancel = nil
		m.tcp = nil
	}
}
func (m *Manager) Close() {
	m.closing.Store(true)
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	m.cancel()
	m.stopTCP()
	for _, rt := range m.Ports {
		m.stop(rt)
	}
	m.wg.Wait()
	m.clockMu.Lock()
	if err := m.clock.Checkpoint(); err != nil {
		_ = m.Store.Event("time", "退出时保存时间检查记录失败："+err.Error())
	}
	m.clockMu.Unlock()
}
func (m *Manager) Views() []cache.View {
	c := m.Config()
	views := m.Cache.Snapshot(time.Now(), c.FastMS, m.TimeTrusted.Load())
	for i := range views {
		views[i].DetailPeriodMS = c.DetailMS
		views[i].CollectionPaused = c.Mode == "maintenance"
	}
	return views
}
func (m *Manager) freezeCommand(slot int, cmd uint16, stop bool, source string) (command.Request, error) {
	if !m.license.Allowed() {
		return command.Request{}, modbus.Busy
	}
	m.mu.RLock()
	defer m.mu.RUnlock()
	action, ok := command.Action(cmd)
	if !ok {
		return command.Request{}, modbus.IllegalValue
	}
	if m.maintenanceBusy() || m.closing.Load() {
		return command.Request{}, modbus.Busy
	}
	if m.cfg.Mode != "normal" || !m.Simulate && !m.HardwareWrites {
		return command.Request{}, modbus.IllegalValue
	}
	for _, meter := range m.cfg.Meters {
		if meter.Slot == slot {
			p := m.cfg.Port(meter.Port)
			if !meter.Enabled || p.Role != config.South {
				return command.Request{}, modbus.IllegalAddress
			}
			rt := m.Ports[meter.Port]
			if rt == nil {
				return command.Request{}, modbus.Busy
			}
			rt.mu.Lock()
			blocked := rt.closing || rt.Error != ""
			rt.mu.Unlock()
			if blocked {
				return command.Request{}, modbus.Busy
			}
			for _, a := range m.cfg.Actions {
				if a == action || cmd == command.LevelAdjustCommand && a == command.LevelAdjustPermission {
					return command.Request{Slot: slot, Binding: meter.Binding, Revision: m.cfg.Revision, Port: meter.Port, Unit: meter.Address, TimeoutMS: p.TimeoutMS, Command: cmd, Action: action, Stop: stop, Source: source}, nil
				}
			}
			return command.Request{}, modbus.IllegalValue
		}
	}
	return command.Request{}, modbus.IllegalAddress
}
func (m *Manager) validateCommand(r command.Request) error {
	current, err := m.freezeCommand(r.Slot, r.Command, r.Stop, r.Source)
	if err != nil {
		return err
	}
	if current.Binding != r.Binding || current.Revision != r.Revision || current.Port != r.Port || current.Unit != r.Unit || current.Action != r.Action {
		return modbus.IllegalValue
	}
	return nil
}
func (m *Manager) poll(ctx context.Context, rt *Runtime, c config.Config) {
	spec := protocols.Load()
	next := map[int]time.Time{}
	detail := map[[2]int]time.Time{}
	failures := map[int]int{}
	cursor := 0
	writes := 0
	start := time.Now()
	round := 0
	slowCursor := 0
	slowCredit := false
	transact := func(meter config.Meter, group int) error {
		rt.Transactions.Add(1)
		requestCtx, cancel := context.WithTimeout(ctx, time.Duration(rt.Port.TimeoutMS)*time.Millisecond)
		defer cancel()
		g := spec.Groups[group]
		w, e := platform.ReadRegisters(requestCtx, rt.Wire, meter.Address, 4, g.Start, g.Words)
		if e != nil {
			rt.Failures.Add(1)
			var ex modbus.Exception
			unsupported := errors.As(e, &ex) && ex == modbus.IllegalAddress
			m.Cache.Fail(meter.Slot, group, e, unsupported)
			return e
		}
		m.Cache.Update(meter.Slot, group, w, m.TimeTrusted.Load())
		return nil
	}
	for ctx.Err() == nil {
		rt.Progress.Store(time.Since(m.Started).Milliseconds())
		now := time.Now()
		due := -1
		for j := 0; j < len(rt.Meters); j++ {
			i := (cursor + j) % len(rt.Meters)
			if !now.Before(next[rt.Meters[i].Slot]) {
				due = i
				break
			}
		}
		if r, ok := m.Engine.Pop(rt.Port.ID, writes >= 3 && due >= 0); ok {
			m.executeCommand(ctx, rt, r)
			writes++
			continue
		}
		if due >= 0 && !slowCredit {
			writes = 0
			meter := rt.Meters[due]
			cursor = (due + 1) % len(rt.Meters)
			success := 0
			all := true
			for g := 0; g < 3; g++ {
				snap := m.Views()[meter.Slot-1]
				if snap.Groups[g].Unsupported {
					all = false
					continue
				}
				e := transact(meter, g)
				if r, ok := m.Engine.Pop(rt.Port.ID, true); ok {
					m.executeCommand(ctx, rt, r)
				}
				if e == nil {
					success++
				} else {
					all = false
					if errors.Is(e, context.DeadlineExceeded) {
						break
					}
				}
			}
			if all {
				m.Cache.Complete(meter.Slot)
			}
			if success == 0 {
				failures[meter.Slot]++
			} else {
				failures[meter.Slot] = 0
			}
			delay := time.Duration(c.FastMS) * time.Millisecond
			if failures[meter.Slot] >= 3 {
				shift := failures[meter.Slot] - 3
				if shift > 3 {
					shift = 3
				}
				delay *= time.Duration(1 << shift)
			}
			next[meter.Slot] = now.Add(delay)
			round++
			if round >= len(rt.Meters) {
				slowCredit = true // Reserve one slow task even when fast polling is continuously due.
				rt.CycleMS.Store(time.Since(start).Milliseconds())
				round = 0
				start = time.Now()
			}
			continue
		}
		// Round-robin all slow groups including parameter snapshots. A saturated
		// bus must not starve details, nor let detail traffic starve parameters.
		slowCredit = false
		work := false
		for j := 0; j < len(rt.Meters)*4; j++ {
			idx := (slowCursor + j) % (len(rt.Meters) * 4)
			meter := rt.Meters[idx/4]
			g := 3 + idx%4
			if g == 3 && !meter.Temperature || g == 4 && !meter.Density || g == 5 && !meter.Analog {
				continue
			}
			key := [2]int{meter.Slot, g}
			if now.Before(detail[key]) {
				continue
			}
			slowCursor = (idx + 1) % (len(rt.Meters) * 4)
			if g < 6 {
				if !m.Views()[meter.Slot-1].Groups[g].Unsupported {
					_ = transact(meter, g)
				}
				detail[key] = time.Now().Add(time.Duration(c.DetailMS) * time.Millisecond)
			} else {
				requestCtx, cancel := context.WithTimeout(ctx, time.Duration(rt.Port.TimeoutMS)*3*time.Millisecond)
				var words []uint16
				var err error
				for _, block := range [][2]uint16{{0, 1}, {130, 3}, {3300, 38}} {
					var part []uint16
					part, err = platform.ReadRegisters(requestCtx, rt.Wire, meter.Address, 3, block[0], block[1])
					if r, ok := m.Engine.Pop(rt.Port.ID, true); ok {
						m.executeCommand(ctx, rt, r)
					}
					rt.Transactions.Add(1)
					if err != nil {
						rt.Failures.Add(1)
						break
					}
					words = append(words, part...)
				}
				cancel()
				m.Cache.Parameters(meter.Slot, words, err)
				detail[key] = time.Now().Add(10 * time.Minute)
			}
			work = true
			break
		}
		if !work && due >= 0 {
			continue
		}
		if !work {
			select {
			case <-ctx.Done():
				return
			case <-time.After(20 * time.Millisecond):
			}
		}
	}
}
func (m *Manager) history() {
	defer m.wg.Done()
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	last := int64(0)
	for {
		select {
		case <-m.ctx.Done():
			return
		case t := <-tick.C:
			if !m.license.Allowed() {
				continue
			}
			c := m.Config()
			bucket := t.Unix() / int64(c.History.PeriodSec)
			if bucket != last {
				last = bucket
				_ = m.Store.History(m.Views(), t.UnixMilli())
			}
			if t.Unix()%60 == 0 {
				_ = m.Store.Cleanup(c.History.RetentionDays, c.History.MaxMB, m.TimeTrusted.Load())
			}
		}
	}
}
func meterMap(c config.Config) map[int]config.Meter {
	out := map[int]config.Meter{}
	for _, v := range c.Meters {
		out[v.Slot] = v
	}
	return out
}
func (m *Manager) Apply(candidate config.Config) error {
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	if m.closing.Load() || m.maintenanceBusy() {
		return modbus.Busy
	}
	m.ApplyingSinceMS.Store(time.Since(m.Started).Milliseconds() + 1)
	defer m.ApplyingSinceMS.Store(0)
	if err := candidate.Validate(); err != nil {
		return err
	}
	old := m.Config()
	oldHost, _, _ := net.SplitHostPort(old.TCP.Listen)
	newHost, _, _ := net.SplitHostPort(candidate.TCP.Listen)
	if newHost != oldHost {
		return fmt.Errorf("TCP接入IP只能通过系统维护的网络设置修改；普通配置仅允许修改服务端口")
	}
	if reflect.DeepEqual(old, candidate) {
		return nil
	}
	if m.Engine.HasCycles() {
		return fmt.Errorf("控制周期未复位或存储异常，禁止修改配置；先记录结果并复位，未知须人工核查")
	}
	if candidate.Revision != old.Revision {
		return fmt.Errorf("配置版本冲突，请重新加载")
	}
	candidate = candidate.Clone()
	candidate.Revision++
	if candidate.Revision == 0 {
		return fmt.Errorf("版本已耗尽")
	}
	candidate.SlotVersions = append([]uint32(nil), old.SlotVersions...)
	a, b := meterMap(old), meterMap(candidate)
	changed := map[int]bool{}
	for _, p := range candidate.Ports {
		if !reflect.DeepEqual(p, old.Port(p.ID)) || old.FastMS != candidate.FastMS || old.DetailMS != candidate.DetailMS || old.Mode != candidate.Mode {
			changed[p.ID] = true
		}
	}
	for slot := 1; slot <= config.MaxMeters; slot++ {
		prev, pok := a[slot]
		cur, cok := b[slot]
		identityChanged := pok != cok || prev.Port != cur.Port || prev.Address != cur.Address || prev.Enabled != cur.Enabled || pok && cok && !reflect.DeepEqual(old.Port(prev.Port), candidate.Port(cur.Port))
		if identityChanged {
			if old.SlotVersions[slot-1] == ^uint32(0) {
				return fmt.Errorf("绑定版本已耗尽")
			}
			candidate.SlotVersions[slot-1]++
		}
		if !reflect.DeepEqual(prev, cur) {
			if pok {
				changed[prev.Port] = true
			}
			if cok {
				changed[cur.Port] = true
			}
		}
	}
	for i := range candidate.Meters {
		candidate.Meters[i].Binding = candidate.SlotVersions[candidate.Meters[i].Slot-1]
	}
	if e := candidate.Validate(); e != nil {
		return e
	}
	stopped := map[int]*Runtime{}
	for id := range changed {
		m.mu.RLock()
		rt := m.Ports[id]
		m.mu.RUnlock()
		m.stop(rt)
		stopped[id] = rt
	}
	opened := map[int]*Runtime{}
	rollback := func() {
		for _, rt := range opened {
			if rt.Wire != nil {
				rt.Wire.Close()
			}
		}
		for id := range stopped {
			rt, e := m.open(old.Port(id), old)
			if e != nil {
				rt = &Runtime{Port: old.Port(id), done: make(chan struct{}), Error: "回退打开失败: " + e.Error()}
				rt.Port.Role = config.Disabled
			}
			m.mu.Lock()
			m.Ports[id] = rt
			m.mu.Unlock()
			m.start(rt, old)
		}
	}
	for id := range stopped {
		rt, e := m.open(candidate.Port(id), candidate)
		if e != nil {
			rollback()
			return fmt.Errorf("应用失败，已尝试恢复旧配置: %w", e)
		}
		opened[id] = rt
	}
	tcpChanged := !reflect.DeepEqual(old.TCP, candidate.TCP)
	if tcpChanged {
		m.stopTCP()
		if e := m.startTCP(candidate); e != nil {
			rollback()
			_ = m.startTCP(old)
			return e
		}
	}
	saveErr := config.SaveAtomic(m.path, candidate)
	var committedErr *config.CommitError
	if saveErr != nil && !errors.As(saveErr, &committedErr) {
		if tcpChanged {
			m.stopTCP()
			_ = m.startTCP(old)
		}
		rollback()
		return saveErr
	}
	m.mu.Lock()
	m.cfg = candidate
	for id, rt := range opened {
		m.Ports[id] = rt
	}
	for slot := 1; slot <= config.MaxMeters; slot++ {
		v, ok := b[slot]
		if !ok {
			m.Cache.Clear(slot)
			continue
		}
		v.Binding = candidate.SlotVersions[slot-1]
		m.Cache.SetConfig(v)
	}
	m.mu.Unlock()
	for _, rt := range opened {
		m.start(rt, candidate)
	}
	_ = m.Store.Event("config", fmt.Sprintf("配置版本%d→%d", old.Revision, candidate.Revision))
	if saveErr != nil {
		_ = m.Store.Event("config_durability", saveErr.Error()+"；运行状态已与替换后的配置一致，请处理存储问题后重新保存")
	}
	return saveErr
}
func (m *Manager) Handle(source string, unit byte, pdu []byte) []byte {
	if !m.license.Allowed() {
		return modbus.Handle(pdu, func(byte, uint16, uint16) ([]uint16, error) { return nil, modbus.Busy }, func(uint16, []uint16) error { return modbus.Busy })
	}
	c := m.Config()
	port := 0
	expected := c.TCP.UnitID
	if strings.HasPrefix(source, "rtu:COM") {
		port, _ = strconv.Atoi(strings.TrimPrefix(source, "rtu:COM"))
		p := c.Port(port)
		if p.Role != config.North {
			return nil
		}
		expected = p.UnitID
	}
	if unit != expected || unit == 0 {
		return nil
	}
	return modbus.Handle(pdu, func(fc byte, a, n uint16) ([]uint16, error) { return m.Registers(fc, a, n) }, func(a uint16, w []uint16) error {
		// Serialize concurrent writes (duplicates still ACK), but never wait behind
		// reconfiguration/shutdown while those operations wait for TCP workers.
		for !m.applyMu.TryLock() {
			if m.closing.Load() || m.ApplyingSinceMS.Load() != 0 {
				return modbus.Busy
			}
			time.Sleep(time.Millisecond)
		}
		defer m.applyMu.Unlock()
		if m.maintenanceBusy() {
			return modbus.Busy
		}
		if m.closing.Load() {
			return modbus.Busy
		}
		current := m.Config()
		allow := current.TCP.AllowWrites
		if port == 0 && (!current.TCP.Enabled || current.TCP.UnitID != unit) {
			return modbus.IllegalAddress
		}
		if port > 0 {
			p := current.Port(port)
			if p.Role != config.North || p.UnitID != unit {
				return modbus.IllegalAddress
			}
			allow = p.AllowWrites
		}
		if int(a) >= protocols.LevelTargetBase && int(a) < protocols.LevelTargetBase+protocols.LevelTargetStride*config.MaxMeters {
			if !allow {
				return modbus.IllegalAddress
			}
			off := int(a) - protocols.LevelTargetBase
			if pdu[0] != 16 || off%protocols.LevelTargetStride != 0 || len(w) != 2 {
				return modbus.IllegalValue
			}
			return m.Engine.StageLevel(off/protocols.LevelTargetStride+1, int32(uint32(w[0])<<16|uint32(w[1])), source)
		}
		offset := int(a) - protocols.SimpleControlBase
		if offset < 0 || offset >= protocols.SimpleControlStride*config.MaxMeters {
			return modbus.IllegalAddress
		}
		// FC10 only allows a single word. Validate the entire shape before mutation.
		if len(w) != 1 {
			return modbus.IllegalValue
		}
		field := offset % protocols.SimpleControlStride
		if field != 0 && field != 7 {
			if pdu[0] == 16 {
				return modbus.IllegalValue
			}
			return modbus.IllegalAddress
		}
		if !allow {
			return modbus.IllegalAddress
		}
		return m.Engine.Submit(offset/protocols.SimpleControlStride+1, field == 7, w[0], source)
	})
}
