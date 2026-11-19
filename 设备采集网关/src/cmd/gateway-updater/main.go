package main

import (
	"flag"
	"levelgateway/src/internal/update"
	"log"
	"os"
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("Usage: gateway-updater worker|recover --base /home/level-gateway --id task-id")
	}
	mode := os.Args[1]
	flags := flag.NewFlagSet(mode, flag.ExitOnError)
	base := flags.String("base", "/home/level-gateway", "安装根目录")
	id := flags.String("id", "", "升级任务编号")
	flags.Parse(os.Args[2:])
	var e error
	switch mode {
	case "worker":
		e = update.RunWorker(*base, *id)
	case "recover":
		e = update.RunRecovery(*base, *id)
	default:
		log.Fatal("未知操作")
	}
	if e != nil {
		log.Fatal(e)
	}
}
