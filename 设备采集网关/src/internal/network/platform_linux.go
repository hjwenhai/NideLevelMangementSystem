package network

import "golang.org/x/sys/unix"

func syncFilesystem() error            { unix.Sync(); return nil }
func fixtureProcessAlive(pid int) bool { return false }
