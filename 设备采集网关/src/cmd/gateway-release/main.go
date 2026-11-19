package main

import (
	"flag"
	"fmt"
	"levelgateway/src/internal/update"
	"log"
	"os"
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("Usage: gateway-release keygen|sign")
	}
	mode := os.Args[1]
	flags := flag.NewFlagSet(mode, flag.ExitOnError)
	private := flags.String("private-key", "private/update-signing.key", "仅厂家电脑保存的签名私钥")
	public := flags.String("public-key", "deploy/update-trust.json", "设备公钥模板")
	payload := flags.String("payload", "", "运行压缩包")
	stage := flags.String("stage", "", "已校验运行目录")
	out := flags.String("output", "", "单文件升级包.gwup")
	notes := flags.String("notes", "网关应用更新，请按更新说明安排维护窗口", "客户更新说明")
	bundle := flags.String("input", "", "校验的签名包")
	arch := flags.String("arch", "linux-arm64", "校验架构")
	flags.Parse(os.Args[2:])
	var e error
	switch mode {
	case "keygen":
		e = update.InitSigningKey(*private, *public)
	case "sign":
		e = update.SignBundle(*payload, *stage, *out, *private, *notes)
	case "verify":
		var v update.Verified
		v, e = update.VerifyBundle(*bundle, *public, *arch, "")
		if e == nil {
			fmt.Printf("Verified %s %s %s\n", v.Manifest.Product, v.Manifest.Release, v.Manifest.Arch)
		}
	default:
		log.Fatal("未知操作")
	}
	if e != nil {
		log.Fatal(e)
	}
	fmt.Println("Release operation completed; signing private key was not exported.")
}
