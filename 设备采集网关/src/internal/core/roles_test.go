package core

import (
	"fmt"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"testing"
)

func TestEveryPortCanBeNorthOrSouth(t *testing.T) {
	m := testManager(t, 16)
	for north := 1; north <= 4; north++ {
		c := m.Config()
		south := []int{}
		for i := range c.Ports {
			p := &c.Ports[i]
			p.Role = config.Disabled
			p.Baud = 4800
			p.AllowWrites = false
			if p.ID == north {
				p.Role = config.North
				p.Baud = 38400
				p.AllowWrites = true
			} else if len(south) < 2 {
				p.Role = config.South
				south = append(south, p.ID)
			}
		}
		for i := range c.Meters {
			c.Meters[i].Port = south[i/8]
			c.Meters[i].Address = byte(i%8 + 1)
		}
		if e := m.Apply(c); e != nil {
			t.Fatal(north, e)
		}
		until(t, func() bool { return m.Views()[15].Valid == 127 })
		reply := m.Handle(fmt.Sprintf("rtu:COM%d", north), 1, modbus.ReadPDU(4, 1000, 120))
		if _, e := modbus.DecodeWords(reply, 120); e != nil {
			t.Fatal(north, e)
		}
		reply = m.Handle(fmt.Sprintf("rtu:COM%d", north), 1, modbus.WritePDU(30007, []uint16{1}))
		if len(reply) != 5 || reply[0] != 16 {
			t.Fatalf("COM%d stop rejected: %x", north, reply)
		}
		until(t, func() bool { return m.Engine.Results()[0].Stop.State == command.Confirmed })
		if reply = m.Handle(fmt.Sprintf("rtu:COM%d", north), 1, modbus.WritePDU(30007, []uint16{0})); len(reply) != 5 {
			t.Fatal("stop reset")
		}
		if reply = m.Handle(fmt.Sprintf("rtu:COM%d", south[0]), 1, modbus.ReadPDU(4, 1000, 24)); reply != nil {
			t.Fatal("south role answered north request")
		}
	}
}
