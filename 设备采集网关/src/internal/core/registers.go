package core

import (
	"levelgateway/protocols"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"time"
)

func (m *Manager) Registers(fc byte, start, count uint16) ([]uint16, error) {
	if count < 1 || count > 125 || int(start)+int(count) > 65536 {
		return nil, modbus.IllegalValue
	}
	c := m.Config()
	now := time.Now()
	trusted := m.TimeTrusted.Load()
	views := m.Cache.Snapshot(now, c.FastMS, trusted)
	spec := protocols.Load()
	requestEnd := int(start) + int(count)
	var region protocols.Region
	found := false
	for _, r := range spec.Regions {
		if int(start) >= r.Base && requestEnd <= r.Base+r.Stride*r.Count {
			region = r
			found = true
			break
		}
	}
	if !found || region.Access == "holding" && fc != 3 {
		return nil, modbus.IllegalAddress
	}
	image := make([]uint16, region.Count*region.Stride)
	switch region.Name {
	case "device":
		image[0] = 0
		image[1] = 3
		image[2] = protocols.MapVersion
		image[3] = config.MaxMeters
		image[4] = uint16(len(c.Meters))
		for _, v := range c.Meters {
			if v.Enabled {
				image[5]++
			}
		}
		modbus.Put64(image[6:], m.Boot)
		modbus.Put64(image[10:], uint64(now.UnixMilli()))
		modbus.Put32(image[14:], c.Revision)
		modbus.Put32(image[16:], uint32(time.Since(m.Started).Seconds()))
		if trusted {
			image[18] = 1
		}
		if m.Simulate {
			image[19] = 1
		}
		if m.Store.Status() != "" || m.Engine.Status() != "" {
			image[20] = 1
		}
		if c.TCP.Enabled {
			image[21] = 1
		}
		led := m.IndicatorStatus()
		for i, mode := range led.Modes {
			image[22+i] = uint16(mode)
		}
		if led.Enabled {
			image[25] = 1
		}
		if led.Error != "" {
			image[26] = 1
		}
		image[27] = config.MaxPerPort
		image[28] = 3
		image[29] = 12
		image[30] = 1 // Remote level target extension is available; permission may be off.
	case "ports":
		pending := map[int]int{}
		for _, p := range c.Ports {
			pending[p.ID] = m.Engine.Pending(p.ID)
		}
		m.mu.RLock()
		for _, p := range c.Ports {
			b := (p.ID - 1) * region.Stride
			image[b] = uint16(p.ID)
			role := uint16(0)
			if p.Role == config.South {
				role = 1
			}
			if p.Role == config.North {
				role = 2
			}
			image[b+1] = role
			modbus.Put32(image[b+2:], uint32(p.Baud))
			image[b+4] = uint16(p.Parity[0])
			image[b+5] = uint16(p.StopBits)
			image[b+6] = uint16(p.UnitID)
			rt := m.Ports[p.ID]
			if rt != nil {
				image[b+7] = uint16(len(rt.Meters))
				modbus.Put32(image[b+8:], uint32(rt.Transactions.Load()))
				modbus.Put32(image[b+10:], uint32(rt.Failures.Load()))
				modbus.Put32(image[b+12:], uint32(rt.CycleMS.Load()))
				image[b+14] = uint16(pending[p.ID])
				rt.mu.Lock()
				if rt.Error != "" {
					image[b+15] = 1
				}
				rt.mu.Unlock()
			}
		}
		m.mu.RUnlock()
	case "summary":
		for i, v := range views {
			copy(image[i*region.Stride:], m.Cache.Summary(v, trusted))
		}
	case "batch_measurements", "batch_measurements_extended":
		if region.Name == "batch_measurements_extended" {
			views = views[32:]
		} else {
			views = views[:32]
		}
		for i, v := range views {
			b := i * region.Stride
			copy(image[b:], v.Raw[:2])
			image[b+2] = v.Raw[26]
		}
	case "batch_status", "batch_status_extended":
		if region.Name == "batch_status_extended" {
			views = views[32:]
		} else {
			views = views[:32]
		}
		for i, v := range views {
			b := i * region.Stride
			image[b] = v.Raw[50]
			image[b+1] = v.Quality
			image[b+2] = v.Valid
		}
	case "detail":
		for i, v := range views {
			copy(image[i*region.Stride:], m.Cache.Detail(v, now))
		}
	case "level_array":
		for i, v := range views {
			copy(image[i*2:], v.Raw[:2])
		}
	case "temperature_array":
		for i, v := range views {
			image[i] = v.Raw[26]
		}
	case "density_array":
		for i, v := range views {
			image[i] = v.Raw[50]
		}
	case "average_density_array":
		for i, v := range views {
			image[i] = v.Raw[49]
		}
	case "quality_array":
		for i, v := range views {
			image[i] = v.Quality
		}
	case "valid_array":
		for i, v := range views {
			image[i] = v.Valid
		}
	case "parameters":
		for i, v := range views {
			b := i * region.Stride
			copy(image[b:], v.Parameters[:])
			modbus.Put64(image[b+42:], uint64(v.ParameterTimeMS))
			if v.ParameterTimeMS != 0 && v.ParameterError == "" {
				image[b+46] = 1
			}
			if v.ParameterError != "" {
				image[b+47] = 1
			}
		}
	case "level_target":
		copy(image, m.Engine.LevelWords())
	case "simple_control":
		words, err := m.Engine.Words()
		if err != nil {
			return nil, err
		}
		copy(image, words)
	default:
		return nil, modbus.IllegalAddress
	}
	offset := int(start) - region.Base
	return append([]uint16(nil), image[offset:offset+int(count)]...), nil
}
