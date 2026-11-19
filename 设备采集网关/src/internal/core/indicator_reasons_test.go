package core

import (
	"levelgateway/src/internal/cache"
	"levelgateway/src/internal/config"
	"strings"
	"testing"
	"time"
)

func TestIndicatorReasonsMatchCollectionAndParameterWarnings(t *testing.T) {
	v := cache.View{Meter: cache.Meter{Config: config.Meter{Slot: 2, Port: 1, Address: 2, Name: "试验表", Enabled: true}}, Quality: 3, Valid: 127}
	v.ParameterError = "响应站号/功能码不匹配"
	modes := indicatorModes([]cache.View{v}, false, true, true, false)
	if modes != [3]int{4, 1, 2} {
		t.Fatal("parameter-only warning altered collection lamp", modes)
	}
	reasons := meterIndicatorReasons(v, false, 3000, true)
	if len(reasons) != 1 || !strings.Contains(reasons[0].Message, "COM1 / 地址2") || !strings.Contains(reasons[0].Message, "参数快照") {
		t.Fatal(reasons)
	}
	v.ParameterError = ""
	v.Quality |= 256
	for i := range v.Groups {
		v.Groups[i].TimeMS = 1
		v.Groups[i].AgeMS = 500
	}
	v.Groups[1].Error = "CRC不匹配"
	reasons = meterIndicatorReasons(v, false, 3000, true)
	if len(reasons) != 1 || !strings.Contains(reasons[0].Message, "IR22～26") || !strings.Contains(reasons[0].Message, "CRC") {
		t.Fatal(reasons)
	}
	if got := meterIndicatorReasons(v, true, 3000, true); len(got) != 0 {
		t.Fatal("maintenance created collection alarm", got)
	}
	v.Quality = 3
	v.Groups[1].Error = ""
	v.Groups[1].Failures = 100
	if got := meterIndicatorReasons(v, false, 3000, true); len(got) != 0 {
		t.Fatal("historical failure remained an active alarm", got)
	}
	v.Quality |= 4 | 256
	v.Groups[2].AgeMS = 10000
	reasons = meterIndicatorReasons(v, false, 3000, true)
	if len(reasons) != 1 || !strings.Contains(reasons[0].Message, "数据过期") {
		t.Fatal(reasons)
	}
}

func TestNorthControlWithFaultKeepsDiagnosticAlarm(t *testing.T) {
	m := testManager(t, 1)
	m.Ports[1].Simulator.SetRaw(1, 52, 7)
	deadline := time.Now().Add(5 * time.Second)
	for m.Views()[0].Quality&8 == 0 {
		if time.Now().After(deadline) {
			t.Fatal("fault not collected")
		}
		time.Sleep(10 * time.Millisecond)
	}
	// Submit an upward command through the regular Modbus TCP listener.
	if _, err := tcpRequest(m.Config().TCP.Listen, []byte{6, 0x75, 0x30, 0, 5}); err != nil {
		t.Fatal(err)
	}
	for m.Engine.Results()[0].Main.State < 3 {
		if time.Now().After(deadline) {
			t.Fatal("control did not finish")
		}
		time.Sleep(10 * time.Millisecond)
	}
	result := m.Engine.Results()[0].Main
	if result.State != 3 || result.Error != 0 || len(result.Readback) != 3 || result.Readback[2] != 7 {
		t.Fatal("fault blocked control or lost diagnostic", result)
	}
	if m.IndicatorStatus().Modes[2] != 1 || m.Views()[0].Raw[52] != 7 {
		t.Fatal("control acknowledgement cleared instrument alarm")
	}
}
