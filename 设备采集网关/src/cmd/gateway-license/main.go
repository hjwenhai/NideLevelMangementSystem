// Factory-only tool: never included in gateway runtime archives.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"levelgateway/src/internal/licensing"
	"log"
	"os"
	"path/filepath"
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("用法：gateway-license keygen|issue|verify")
	}
	mode := os.Args[1]
	f := flag.NewFlagSet(mode, flag.ExitOnError)
	private := f.String("private-key", "private/license-signing.key", "仅厂家保存的私钥")
	public := f.String("public-key", "src/internal/licensing/trusted-public.json", "厂家公钥")
	request := f.String("request", "", "设备申请.gwreq")
	customer := f.String("customer", "", "客户名称")
	out := f.String("output", "", "输出.gwlic")
	input := f.String("input", "", "待核对的.gwlic")
	registry := f.String("registry", "", "厂家签发台账，默认私钥目录license-issued.jsonl")
	f.Parse(os.Args[2:])
	var err error
	switch mode {
	case "keygen":
		err = licensing.InitKey(*private, *public)
	case "issue":
		var b []byte
		b, err = os.ReadFile(*request)
		var req licensing.Request
		if err == nil {
			err = licensing.Decode(b, &req)
		}
		if err == nil {
			err = req.Validate()
		}
		if err == nil {
			var key []byte
			key, err = licensing.ReadPrivate(*private)
			if err == nil {
				var env licensing.Envelope
				env, err = licensing.NewGrant(req, *customer, key)
				if err == nil {
					b, _ = json.MarshalIndent(env, "", "  ")
					err = writeNew(*out, append(b, '\n'))
					if err == nil {
						if *registry == "" {
							*registry = filepath.Join(filepath.Dir(*private), "license-issued.jsonl")
						}
						var file *os.File
						file, err = os.OpenFile(*registry, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0600)
						if err == nil {
							line, _ := json.Marshal(env.License)
							_, err = file.Write(append(line, '\n'))
							if err == nil {
								err = file.Sync()
							}
							file.Close()
						}
					}
				}
			}
		}
	case "verify":
		var b []byte
		b, err = os.ReadFile(*public)
		var keys licensing.PublicKeys
		if err == nil {
			err = json.Unmarshal(b, &keys)
		}
		if err == nil {
			b, err = os.ReadFile(*input)
		}
		if err == nil {
			var g licensing.Grant
			g, err = licensing.VerifySignature(b, keys)
			if err == nil {
				fmt.Printf("签名有效；客户=%s；设备码=%s；永久授权。设备本机仍须核对硬件。\n", g.Customer, g.DeviceCode)
			}
		}
	default:
		err = fmt.Errorf("未知操作")
	}
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("操作完成，未导出厂家私钥。")
}
func writeNew(path string, b []byte) error {
	if path == "" {
		return fmt.Errorf("请指定输出文件")
	}
	if e := os.MkdirAll(filepath.Dir(path), 0700); e != nil {
		return e
	}
	f, e := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	_, e = f.Write(b)
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	return e
}
