# HTTPS证书与可选启用说明

2026-10-19。适用软件0.3.6及后续兼容版本；本次只生成备用证书，不修改程序或安装包，不向设备部署，不给电脑或手机安装信任。

## 1 默认继续使用HTTP

用户已明确：**默认不使用HTTPS。** 设备的config/service.env继续保持以下两项为空，其他已保存参数保持原值：

~~~ini
# 默认使用HTTP，不指定HTTPS证书。
TLS_CERT=

# 默认使用HTTP，不指定HTTPS私钥。
TLS_KEY=
~~~

当前仍访问http://192.168.0.230:8088。证书保存到电脑，不必为本次文件生成操作重启网关。程序模板及0.3.6-20261018-r1安装包的TLS两项本来就是空值，不需要重新制作包。

## 2 已生成的文件

目录：[output/https/GT6757S-20261019-r1](../output/https/GT6757S-20261019-r1/README.md)。本套证书使用厂家本地根CA签发，不是公共互联网证书。

| 文件 | 用途与保管位置 |
| --- | --- |
| device/server.crt | 设备HTTPS服务器证书；只有决定开通HTTPS时才上传。 |
| device/server.key | 与服务器证书匹配的私钥；只有决定开通时才上传，不能当客户资料发送。 |
| client/GT6757X-Local-Root-CA.cer | DER格式根证书，供Windows等客户端安装信任；不是私钥。 |
| client/GT6757X-Local-Root-CA.crt | 相同根证书的PEM格式，供需要此格式的客户端使用。 |
| manufacturer-private/ca.key | 根CA私钥，留在厂家受控电脑，用于后续签发；不上传网关，不发客户。 |
| manifest.json | 两个IP、有效期及公开证书SHA256指纹，便于核对；不含私钥内容。 |

服务器证书包含IP地址SAN：192.168.0.230、192.168.1.230。到期时间为**北京时间2027-10-19 23:59:59**；根证书到期为2036-10-19 23:59:59。IP变更或到期前需要重新签发相应服务器证书，不能只修改文件名。网关和访问终端的日期也须正确。

两把私钥均为无口令PEM文件，便于服务无人值守启动；安全性依靠文件权限和保管。本次电脑证书目录已限制为当前Windows账号、SYSTEM及Administrators访问。备份时同时保存根证书和根私钥，私钥备份单独受控。不要把整个目录压缩发给客户。

## 3 可选：以后决定开通HTTPS时

**当前不执行本节。** 以下是首次开通操作，需使用设备已有IP和实际SSH连接方式。若目标路径已有证书，先保留原证书/私钥和服务配置，再更换。

先在设备终端准备私有目录：

~~~sh
# 创建HTTPS运行文件目录，证书是运行配置，用户文档仍留在电脑。
mkdir -p /home/level-gateway/config/tls

# 目录只允许设备root账号访问。
chmod 700 /home/level-gateway/config/tls
~~~

再在电脑PowerShell上传，只有下面两个文件：

~~~powershell
# 进入电脑上的网关项目目录。
Set-Location -LiteralPath 'E:/Works/耐得/耐德东京计装/液位管理系统_20260704/设备采集网关'

# 上传服务器证书，不上传厂家根私钥。
scp ./output/https/GT6757S-20261019-r1/device/server.crt root@192.168.0.230:/home/level-gateway/config/tls/server.crt

# 上传与服务器证书配套的私钥，经SSH加密传输。
scp ./output/https/GT6757S-20261019-r1/device/server.key root@192.168.0.230:/home/level-gateway/config/tls/server.key
~~~

设备终端设置文件权限并备份服务配置：

~~~sh
# 证书和私钥都只允许设备root账号读写。
chmod 600 /home/level-gateway/config/tls/server.crt /home/level-gateway/config/tls/server.key

# 保存启用HTTPS前的服务配置，便于核对和恢复。
cp -p /home/level-gateway/config/service.env "/home/level-gateway/config/service.before-https-$(date -u +%Y%m%dT%H%M%SZ).env"
~~~

用设备文本编辑器修改config/service.env中的TLS两项。以下是文件内容，不是直接执行后就会保存的命令：

~~~ini
# 可选HTTPS启用时指定服务器证书。
TLS_CERT=/home/level-gateway/config/tls/server.crt

# 可选HTTPS启用时指定配套私钥。
TLS_KEY=/home/level-gateway/config/tls/server.key
~~~

保持WEB_LISTEN为实际使用网口的原地址，开通HTTPS不会自动把它改成443或自动绑定两个网口。证书覆盖两个IP，不等于网页自动同时监听两个IP。确认没有正在执行的控制后，在设备终端重启：

~~~sh
# 重启使HTTPS设置生效，采集、网页和Modbus通讯会短暂中断。
sh /home/level-gateway/current/deploy/service.sh restart

# 查看网页启动日志，发现证书路径或配套错误时先恢复原设置。
tail -n 30 /home/level-gateway/logs/web.log
~~~

若仍监听192.168.0.230:8088，则地址改为https://192.168.0.230:8088。原http地址不再是正常管理入口；本程序不提供自动HTTP跳转。若实际绑定的是192.168.1.230，则使用相应的https地址。

## 4 可选：访问终端信任根证书

只有决定使用HTTPS时才进行。安装的是client目录中的公开根证书，不能安装或分发server.key、ca.key。先核对manifest.json中的根证书SHA256指纹。

Windows可双击GT6757X-Local-Root-CA.cer，选择“安装证书→当前用户→将所有的证书都放入下列存储→受信任的根证书颁发机构”，由使用者核对并确认信任，然后重新打开浏览器。不同浏览器/企业管理策略可能使用单独证书库，需以实际访问结果核对。手机和平板也要单独安装并信任这张根证书，具体操作随系统和管理策略而异；生成证书不会自动建立信任。

若仍出现证书错误，核对根证书是否正确、访问IP是否在上述两个地址内、电脑/网关时间及证书有效期，不通过忽略浏览器警告来验收。HTTPS保护网页连接，Modbus TCP仍按原协议运行，不因TLS两项而加密。

## 5 恢复HTTP

将config/service.env中的TLS_CERT、TLS_KEY同时清空，保持其余参数不变；无执行中控制时按上面的重启命令重启，再访问原http地址。无需删除证书文件或修改数据库。

## 6 已完成的检查

签名、证书/私钥匹配、PEM/DER根证书一致、IP SAN及有效期检查通过；使用当前已编译Go网页程序在本机回环地址验证两个IP名称的HTTPS握手、错误IP拒绝、未信任根证书拒绝、HTTPS登录和Secure会话Cookie，共12项通过。

证据：[HTTPS证书检查记录](../tests/https-certificate-verification-20261019.json)。没有连接GT6757S，没有安装系统根信任，没有修改设备IP、时钟或服务配置；实体设备及实际手机浏览器访问尚未验证。
