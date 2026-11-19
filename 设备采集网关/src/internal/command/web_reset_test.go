package command

import (
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/modbus"
	"reflect"
	"testing"
)

func TestWebResetFailedAndUncertainBothChannels(t *testing.T) {
	for _, state := range []int{Failed, Uncertain} {
		for _, stop := range []bool{false, true} {
			for _, slot := range []int{1, 64} {
				t.Run(fmt.Sprintf("state%d_stop%t_slot%d", state, stop, slot), func(t *testing.T) {
					e, store, sim := fixture(t)
					port := (slot-1)/16 + 1
					for _, ch := range []bool{false, true} {
						value := uint16(6)
						if ch {
							value = 1
						}
						if err := e.Submit(slot, ch, value, "old-control"); err != nil {
							t.Fatal(err)
						}
						r, ok := e.Pop(port, false)
						if !ok {
							t.Fatal("no fixture request")
						}
						code := uint16(2)
						if state == Uncertain {
							code = 9
						}
						if !e.update(r, state, code, "保存的控制结果", nil) {
							t.Fatal("fixture update")
						}
					}
					before := e.Results()[slot-1]
					selected := channel(&before, stop)
					for _, bad := range []struct{ id, operator, reason string }{{"stale", "01", "现场核查完成"}, {selected.Request.ID, "", "现场核查完成"}, {selected.Request.ID, "01", ""}} {
						if err := e.MaintenanceReset(slot, stop, bad.id, bad.operator, bad.reason); err != modbus.IllegalValue {
							t.Fatal("invalid review accepted", err)
						}
					}
					if err := e.MaintenanceReset(slot, !stop, selected.Request.ID, "01", "现场核查完成"); err != modbus.IllegalValue {
						t.Fatal("wrong channel accepted", err)
					}
					if !reflect.DeepEqual(before, e.Results()[slot-1]) {
						t.Fatal("rejection mutated cycle")
					}
					if err := e.MaintenanceReset(slot, stop, selected.Request.ID, "核查01", "现场核对，只清结果不重发"); err != nil {
						t.Fatal(err)
					}
					after := e.Results()[slot-1]
					cleared := channel(&after, stop)
					if cleared.State != Idle || cleared.Error != 0 || cleared.Value != 0 || cleared.Sent || cleared.Count != selected.Count {
						t.Fatal("incorrect reset", cleared)
					}
					if !reflect.DeepEqual(channel(&before, !stop), channel(&after, !stop)) {
						t.Fatal("other channel changed")
					}
					if sim.Count(1) != 0 || e.Pending(port) != 0 {
						t.Fatal("reset sent/queued action")
					}
					if _, ok := e.Pop(port, false); ok {
						t.Fatal("reset queued action")
					}
					if err := e.MaintenanceReset(slot, stop, selected.Request.ID, "01", "重复提交旧表单"); err != modbus.IllegalValue {
						t.Fatal("duplicate review accepted", err)
					}
					var audit map[string]json.RawMessage
					events, _ := store.Events()
					found := false
					for _, event := range events {
						if event["kind"] != "control_maintenance_reset" {
							continue
						}
						if err := json.Unmarshal([]byte(event["message"].(string)), &audit); err != nil {
							t.Fatal(err)
						}
						var original, result Slot
						json.Unmarshal(audit["before"], &original)
						json.Unmarshal(audit["after"], &result)
						if !reflect.DeepEqual(original, before) || !reflect.DeepEqual(result, after) || string(audit["operator"]) != `"核查01"` || string(audit["confirmation_record"]) != `"gateway-web"` {
							t.Fatal("incomplete audit", event["message"])
						}
						found = true
					}
					if !found {
						t.Fatal("missing reset audit")
					}
					restored := newTestEngine(store)
					if err := restored.Recover(); err != nil || !reflect.DeepEqual(restored.Results()[slot-1], after) {
						t.Fatal("reset not durable", err)
					}
				})
			}
		}
	}
}

func TestWebResetRejectsNonResettableStatesAndStoreFailure(t *testing.T) {
	for _, state := range []int{Idle, Accepted, Running, Confirmed} {
		t.Run(fmt.Sprint(state), func(t *testing.T) {
			e, _, sim := fixture(t)
			if state != Idle {
				e.Submit(1, false, 5, "fixture")
			}
			if state == Running {
				e.Pop(1, false)
			}
			if state == Confirmed {
				run(t, e, sim, 1)
			}
			before := e.Results()[0]
			if err := e.MaintenanceReset(1, false, before.Main.Request.ID, "01", "核对现场后复位"); err != modbus.IllegalValue {
				t.Fatal("state accepted", state, err)
			}
			if !reflect.DeepEqual(before, e.Results()[0]) {
				t.Fatal("rejected reset changed state")
			}
		})
	}
	e, store, _ := fixture(t)
	e.Submit(1, false, 6, "fixture")
	r, _ := e.Pop(1, false)
	e.update(r, Failed, 2, "旧版非远程未发送", nil)
	before := e.Results()[0]
	store.Close()
	if err := e.MaintenanceReset(1, false, r.ID, "01", "核对现场后复位"); err != modbus.DeviceFailure {
		t.Fatal("store failure hidden", err)
	}
	if !reflect.DeepEqual(before, e.Results()[0]) {
		t.Fatal("failed audit cleared result")
	}
}
