package main

import (
	"context"
	"flag"
	"levelgateway/src/internal/network"
	"levelgateway/src/internal/update"
	"levelgateway/src/internal/web"
	"log"
	"net/http"
	"os"
	"os/signal"
	"runtime"
	"syscall"
	"time"
)

func main() {
	listen := flag.String("listen", "127.0.0.1:8088", "网页监听地址")
	ipc := flag.String("ipc", "127.0.0.1:19090", "core内部API")
	token := flag.String("token-file", "data/ipc.token", "内部令牌路径")
	auth := flag.String("auth-file", "data/admin.json", "管理员凭据")
	init := flag.Bool("init-admin", false, "从标准输入读取密码，初始化后退出")
	cert := flag.String("tls-cert", "", "HTTPS证书")
	key := flag.String("tls-key", "", "HTTPS私钥")
	upgradeBase := flag.String("upgrade-base", "", "升级根目录，空值禁用网页升级")
	fixture := flag.Bool("upgrade-fixture", false, "仅Windows本机升级测试")
	networkBase := flag.String("network-base", "", "双网口管理模式，Linux网页监听全部IPv4网口")
	flag.Parse()
	if *networkBase != "" && runtime.GOOS == "linux" {
		var err error
		*listen, err = network.Wildcard(*listen)
		if err != nil {
			log.Fatal(err)
		}
	}

	if *init {
		password, e := web.ReadPassword()
		if e == nil {
			e = web.InitAdmin(*auth, password)
		}
		if e != nil {
			log.Fatal(e)
		}
		log.Print("管理员凭据已初始化")
		return
	}
	s, e := web.New(*ipc, *token, *auth)
	if e != nil {
		log.Fatal(e)
	}
	if *upgradeBase != "" {
		u, err := update.New(update.Options{Base: *upgradeBase, Fixture: *fixture})
		if err != nil {
			log.Fatal(err)
		}
		s.ConfigureUpgrade(u)
	}
	if (*cert == "") != (*key == "") {
		log.Fatal("证书和私钥须同时提供")
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	server := http.Server{Addr: *listen, Handler: s.Handler(), ReadHeaderTimeout: 3 * time.Second, ReadTimeout: 180 * time.Second, WriteTimeout: 240 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
	go func() {
		<-ctx.Done()
		c, stop := context.WithTimeout(context.Background(), 3*time.Second)
		defer stop()
		_ = server.Shutdown(c)
	}()
	log.Printf("gateway-web listen=%s", *listen)
	if *cert != "" {
		e = server.ListenAndServeTLS(*cert, *key)
	} else {
		e = server.ListenAndServe()
	}
	if e != nil && e != http.ErrServerClosed {
		log.Fatal(e)
	}
}
