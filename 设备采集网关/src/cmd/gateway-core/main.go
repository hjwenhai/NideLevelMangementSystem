package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"flag"
	"fmt"
	"levelgateway/src/internal/clockstate"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/core"
	"levelgateway/src/internal/licensing"
	"levelgateway/src/internal/network"
	"levelgateway/src/internal/platform"
	"levelgateway/src/internal/storage"
	"levelgateway/src/internal/update"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"
)

func main() {
	path := flag.String("config", "deploy/config.demo.json", "配置文件")
	db := flag.String("db", "data/gateway.db", "数据库")
	backup := flag.String("backup-db", "", "只制作SQLite一致性备份后退出，不迁移源库，不覆盖目标")
	simulate := flag.Bool("simulate", false, "使用模拟仪表，不打开物理串口")
	hardwareWrites := flag.Bool("hardware-writes", false, "启用已验证真实动作；另需配置白名单及接口许可")
	clockSet := flag.Bool("allow-clock-set", false, "允许管理员修改Linux系统时钟")
	trusted := flag.Bool("time-trusted", false, "已废弃；时间由校时记录和RTC检查自动确认")
	rtcDevice := flag.String("rtc-device", "/dev/rtc0", "UTC硬件时钟设备")
	clockState := flag.String("clock-state", "", "校时记录路径；默认数据库同目录clock-state.json")
	watchdog := flag.String("watchdog", "", "已核验的看门狗设备路径；默认不打开")
	serviceScript := flag.String("service-script", "", "受控服务脚本绝对路径；空值禁用网页程序重启")
	serviceShell := flag.String("service-shell", "/bin/sh", "服务脚本Shell绝对路径")
	restartWorker := flag.Bool("program-restart-worker", false, "独立程序重启任务；仅由服务内部调用")
	leds := flag.Bool("leds", true, "启用SYS1～SYS3状态灯；模拟模式不访问硬件，可用--leds=false关闭")
	ipc := flag.String("ipc", "127.0.0.1:19090", "本机API监听地址或unix:/path")
	tokenFile := flag.String("token-file", "data/ipc.token", "本机API令牌文件")
	generate := flag.String("init-config", "", "生成默认配置后退出")
	initListen := flag.String("init-tcp-listen", "127.0.0.1:1502", "生成配置时的TCP监听地址，不改变已有配置")
	initAddress := flag.Int("init-first-address", 1, "单台首次配置的仪表站址，1～243")
	initBaud := flag.Int("init-south-baud", 4800, "生成配置时的南向波特率")
	checkListen := flag.String("check-listen", "", "只检查IP:端口格式后退出，不修改系统网络")
	n := flag.Int("meters", 64, "生成配置仪表数量，1～64")
	check := flag.Bool("check-config", false, "只校验配置后退出")
	migrate := flag.Bool("migrate-config", false, "将已校验配置原子保存为schema3；请先备份")
	health := flag.Bool("health-check", false, "检查指定IPC采集服务后退出")
	upgradeBase := flag.String("upgrade-base", "", "升级根目录，空值禁用网页升级")
	upgradeWeb := flag.String("upgrade-web-listen", "127.0.0.1:8088", "升级检查的本机网页监听")
	upgradeCert := flag.String("upgrade-tls-cert", "", "本机HTTPS检查证书")
	fixture := flag.Bool("upgrade-fixture", false, "仅Windows模拟升级测试")
	networkBase := flag.String("network-base", "", "系统网络管理根目录，空值禁用")
	networkWorker := flag.Bool("network-worker", false, "独立网络修改执行器")
	networkCheck := flag.Bool("network-check", false, "检查是否存在未结束网络事务，不修改配置")
	networkRecover := flag.Bool("network-recover", false, "启动前恢复未确认的网络事务")
	networkJob := flag.String("network-job", "", "内部网络任务编号")
	networkFixture := flag.Bool("network-fixture", false, "仅Windows本机模拟，不修改系统网络")
	licenseFile := flag.String("license-file", "", "永久授权文件，默认配置目录license.json")
	licenseRequest := flag.String("license-request", "", "只导出本机申请文件后退出；-表示标准输出，不接触仪表")
	checkLicense := flag.Bool("check-license", false, "只检查本机永久授权后退出")
	licenseTestID := flag.String("license-test-id", "", "仅Windows模拟测试硬件标识；Linux拒绝此参数")
	flag.Parse()
	if *licenseFile == "" {
		*licenseFile = filepath.Join(filepath.Dir(*path), "license.json")
	}
	var licenseReader licensing.Reader
	if *licenseTestID != "" {
		if runtime.GOOS != "windows" || !*simulate {
			log.Fatal("授权测试标识仅允许Windows模拟仪表")
		}
		licenseReader = func(kind string) (string, error) {
			if kind != "soc-serial" {
				return "", fmt.Errorf("测试不支持此类型")
			}
			return *licenseTestID, nil
		}
	}
	license := licensing.New(*licenseFile, !*simulate || *licenseTestID != "", licenseReader)
	if *licenseRequest != "" {
		b, e := license.RequestJSON()
		if e != nil {
			log.Fatal(e)
		}
		b = append(b, '\n')
		if *licenseRequest == "-" {
			fmt.Print(string(b))
		} else {
			if _, e := os.Lstat(*licenseRequest); e == nil {
				log.Fatal("申请文件已存在，请更换路径")
			}
			if e = config.WriteAtomic(*licenseRequest, b); e != nil {
				log.Fatal(e)
			}
		}
		return
	}
	if *checkLicense {
		if e := license.CheckFile(); e != nil {
			log.Fatal(e)
		}
		fmt.Println("本机永久授权校验通过")
		return
	}
	var networking *network.Service
	if *networkBase != "" {
		if *networkFixture && !*simulate {
			log.Fatal("网络测试必须使用模拟仪表")
		}
		var err error
		networking, err = network.New(network.Options{Base: *networkBase, Fixture: *networkFixture})
		if err != nil {
			log.Fatal(err)
		}
	}
	if *networkCheck {
		if networking != nil && networking.Busy() {
			log.Fatal("网络事务未结束，请先完成确认或恢复")
		}
		return
	}
	if *networkWorker || *networkRecover {
		if networking == nil {
			log.Fatal("网络执行器缺少根目录")
		}
		var err error
		if *networkRecover {
			err = networking.RecoverBoot()
		} else {
			err = networking.RunWorker(*networkJob, networking.Ops(*serviceShell, *serviceScript), network.TrialSeconds*time.Second)
		}
		if err != nil {
			log.Fatal(err)
		}
		return
	}
	if networking != nil && !*networkFixture {
		if _, port, err := net.SplitHostPort(*upgradeWeb); err == nil {
			*upgradeWeb = net.JoinHostPort("127.0.0.1", port)
		}
	}

	if *restartWorker {
		if err := platform.RunProgramRestartWorker(*serviceScript, *serviceShell); err != nil {
			log.Fatal(err)
		}
		return
	}
	if *checkListen != "" {
		host, port, err := net.SplitHostPort(*checkListen)
		p, numberErr := strconv.Atoi(port)
		if err != nil || net.ParseIP(host) == nil || numberErr != nil || p < 1 || p > 65535 {
			log.Fatal("监听地址须为IP:端口，端口1～65535")
		}
		fmt.Println("valid listen address")
		return
	}
	if *backup != "" {
		if err := storage.BackupDatabase(*db, *backup); err != nil {
			log.Fatal(err)
		}
		fmt.Println("database backup complete")
		return
	}
	if *health {
		if e := healthCheck(*ipc); e != nil {
			log.Fatal(e)
		}
		fmt.Println("healthy")
		return
	}
	if *generate != "" {
		if *n < 1 || *n > config.MaxMeters {
			log.Fatal("meters须1～64")
		}
		c := config.Default(*n)
		c.TCP.Listen = *initListen
		if *initAddress < 1 || *initAddress > 243 || *n != 1 && *initAddress != 1 {
			log.Fatal("自定首台站址仅用于单台配置，范围1～243")
		}
		if *n == 1 {
			c.Meters[0].Address = uint8(*initAddress)
		}
		for i := range c.Ports {
			if c.Ports[i].Role == config.South {
				c.Ports[i].Baud = *initBaud
			}
		}
		if e := config.SaveAtomic(*generate, c); e != nil {
			log.Fatal(e)
		}
		fmt.Println(*generate)
		return
	}
	c, e := config.Load(*path)
	if e != nil {
		log.Fatal(e)
	}
	if *migrate {
		if err := config.SaveAtomic(*path, c); err != nil {
			log.Fatal(err)
		}
		fmt.Println("configuration schema3 saved; bindings preserved")
		return
	}
	if *check {
		fmt.Println("valid config")
		return
	}
	if !core.SafeLocalAddress(*ipc) {
		log.Fatal("内部API只允许本机地址或Unix socket")
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if *clockState == "" {
		*clockState = filepath.Join(filepath.Dir(*db), "clock-state.json")
	}
	clock := clockstate.New(*clockState, *rtcDevice, *simulate, clockstate.Ops{Now: time.Now, Sleep: time.Sleep, SetSystem: platform.SetClock, ReadRTC: platform.ReadRTC, WriteRTC: platform.WriteRTC})
	if *trusted {
		log.Print("--time-trusted已废弃，不绕过RTC及校时记录检查")
	}
	clock.Restore(*clockSet)
	log.Printf("clock trusted=%t source=%s reason=%s warning=%s", clock.Status.Trusted, clock.Status.Source, clock.Status.Reason, clock.Status.Warning)
	s, e := storage.Open(*db)
	if e != nil {
		log.Fatal(e)
	}
	defer s.Close()
	var upgrades *update.Service
	if *upgradeBase != "" {
		if *fixture && !*simulate {
			log.Fatal("升级测试必须使用模拟仪表")
		}
		webURL, err := update.WebURL(*upgradeWeb, *upgradeCert)
		if err != nil {
			log.Fatal(err)
		}
		upgrades, err = update.New(update.Options{Base: *upgradeBase, IPC: *ipc, WebURL: webURL, TLSCert: *upgradeCert, Shell: *serviceShell, ServiceScript: *serviceScript, Fixture: *fixture})
		if err != nil {
			log.Fatal(err)
		}
	}
	m, e := core.NewWithLicense(ctx, c, *path, s, *simulate, *hardwareWrites, upgrades, networking, license, clock)
	if e != nil {
		log.Fatal(e)
	}
	defer m.Close()
	if *serviceScript != "" {
		m.ConfigureProgramRestart(func() (func() error, error) {
			cmd, err := platform.StartProgramRestart(*serviceScript, *serviceShell, filepath.Join(filepath.Dir(*db), "..", "logs", "maintenance.log"))
			if err != nil {
				return nil, err
			}
			return cmd.Wait, nil
		})
	}
	if networking != nil {
		m.ConfigureNetwork(networking, func(id string) error {
			child, err := platform.StartNetworkWorker(*networkBase, id, *serviceScript, *serviceShell, *networkFixture)
			if err == nil {
				go func() { _ = child.Wait() }()
			}
			return err
		})
	}
	m.StartClockMonitor()
	if *leds {
		m.StartIndicators()
	}
	if *watchdog != "" {
		go func() {
			if err := m.Watchdog(ctx, *watchdog); err != nil {
				log.Print(err)
				cancel()
			}
		}()
	}
	tokenBytes := make([]byte, 32)
	if _, e = rand.Read(tokenBytes); e != nil {
		log.Fatal(e)
	}
	token := hex.EncodeToString(tokenBytes)
	if e = config.WriteAtomic(*tokenFile, []byte(token)); e != nil {
		log.Fatal(e)
	}
	defer os.Remove(*tokenFile)
	network, address := "tcp", *ipc
	if strings.HasPrefix(address, "unix:") {
		network = "unix"
		address = strings.TrimPrefix(address, "unix:")
		if e = os.MkdirAll(filepath.Dir(address), 0700); e != nil {
			log.Fatal(e)
		}
		if _, e = os.Stat(address); e == nil {
			log.Fatal("内部socket已存在，请确认原进程停止后清理")
		}
		defer os.Remove(address)
	}
	l, e := net.Listen(network, address)
	if e != nil {
		log.Fatal(e)
	}
	if network == "unix" {
		_ = os.Chmod(address, 0600)
	}
	server := http.Server{Handler: m.API(token, *clockSet), ReadHeaderTimeout: 3 * time.Second, ReadTimeout: 10 * time.Second, WriteTimeout: 15 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
	go func() {
		<-ctx.Done()
		shutdownCtx, stop := context.WithTimeout(context.Background(), 3*time.Second)
		defer stop()
		_ = server.Shutdown(shutdownCtx)
	}()
	log.Printf("gateway-core simulate=%v map=3 north=%s IPC=%s", *simulate, c.TCP.Listen, *ipc)
	if e = server.Serve(l); e != nil && e != http.ErrServerClosed {
		log.Print(e)
	}
}
