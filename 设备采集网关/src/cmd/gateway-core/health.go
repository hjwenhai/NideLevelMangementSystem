package main

import (
	"context"
	"fmt"
	"io"
	"levelgateway/src/internal/core"
	"net"
	"net/http"
	"strings"
	"time"
)

func healthCheck(ipc string) error {
	if !core.SafeLocalAddress(ipc) {
		return fmt.Errorf("健康检查只允许本机IPC")
	}
	transport := &http.Transport{}
	url := "http://" + ipc + "/healthz"
	if strings.HasPrefix(ipc, "unix:") {
		path := strings.TrimPrefix(ipc, "unix:")
		transport.DialContext = func(ctx context.Context, _, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(ctx, "unix", path)
		}
		url = "http://gateway/healthz"
	}
	client := &http.Client{Transport: transport, Timeout: 3 * time.Second}
	response, err := client.Get(url)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		b, _ := io.ReadAll(io.LimitReader(response.Body, 1024))
		return fmt.Errorf("health: %s", b)
	}
	return nil
}
