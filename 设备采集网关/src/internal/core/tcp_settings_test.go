package core

import (
	"encoding/json"
	"levelgateway/src/internal/modbus"
	"net"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
)

func TestTCPHostIsNetworkOnlyAndPortCanChange(t *testing.T) {
	m := testManager(t, 1)
	before := m.Config()
	candidate := before.Clone()
	candidate.TCP.Listen = "0.0.0.0:502"
	if e := m.Apply(candidate); e == nil || !strings.Contains(e.Error(), "网络设置") {
		t.Fatal("host change accepted", e)
	}
	if !reflect.DeepEqual(before, m.Config()) {
		t.Fatal("rejected host change mutated configuration")
	}
	if _, e := tcpRequest(before.TCP.Listen, modbus.ReadPDU(4, 0, 4)); e != nil {
		t.Fatal("rejected host change disrupted original listener", e)
	}
	listener, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	next := listener.Addr().String()
	listener.Close()
	candidate = before.Clone()
	candidate.TCP.Listen = next
	if e := m.Apply(candidate); e != nil {
		t.Fatal("port change rejected", e)
	}
	if m.Config().TCP.Listen != next || !reflect.DeepEqual(before.Meters, m.Config().Meters) || !reflect.DeepEqual(before.Ports, m.Config().Ports) {
		t.Fatal("port change affected instruments")
	}
	if _, e := tcpRequest(next, modbus.ReadPDU(4, 0, 4)); e != nil {
		t.Fatal("new port unavailable", e)
	}
	api := m.API("t")
	r := httptest.NewRequest("GET", "/api/status", nil)
	r.Header.Set("Authorization", "Bearer t")
	w := httptest.NewRecorder()
	api.ServeHTTP(w, r)
	var status struct {
		TCP struct {
			Listen string `json:"listen"`
		}
	}
	if e := json.Unmarshal(w.Body.Bytes(), &status); e != nil || status.TCP.Listen != next {
		t.Fatal("status does not expose saved TCP endpoint", e, w.Body.String())
	}
	// Direct API clients cannot bypass the single network entry point.
	candidate = m.Config()
	candidate.TCP.Listen = "192.168.50.20:502"
	body, _ := json.Marshal(candidate)
	r = httptest.NewRequest("PUT", "/api/config", strings.NewReader(string(body)))
	r.Header.Set("Authorization", "Bearer t")
	w = httptest.NewRecorder()
	api.ServeHTTP(w, r)
	if w.Code != 409 || m.Config().TCP.Listen != next {
		t.Fatal("API host override", w.Code, w.Body.String())
	}
}
