using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Naide.LevelManager.Domain;
internal static class VendorAuthorization
{
    public static int Run(string[] args)
    {
        string? Option(string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
        string Required(string key)=>Option(key)??throw new ArgumentException("缺少参数 "+key);
        static void NewFile(string path,string text)
        {
            if(!Path.IsPathFullyQualified(path))throw new ArgumentException("请使用明确的绝对输出路径");Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);using var writer=new StreamWriter(stream,new UTF8Encoding(false));writer.Write(text);writer.Flush();stream.Flush(true);
        }
        if(args[0]=="authorization-keygen")
        {
            var directory=Required("--output");if(!Path.IsPathFullyQualified(directory))throw new ArgumentException("请指定新的绝对密钥目录");var privateFile=Path.Combine(directory,"level-manager-signing.pem");var publicFile=Path.Combine(directory,"level-manager-public.pem");if(File.Exists(privateFile)||File.Exists(publicFile))throw new ArgumentException("不覆盖已有签名密钥");
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);NewFile(privateFile,key.ExportPkcs8PrivateKeyPem());NewFile(publicFile,key.ExportSubjectPublicKeyInfoPem());Console.WriteLine("已生成供方签名密钥。私钥仅留供方，不复制到客户运行目录。");Console.WriteLine("公钥须加入本产品受信任配置后重新构建；不会自动替换已交付客户端公钥。");Console.WriteLine(publicFile);return 0;
        }
        if(args[0]!="authorization-issue")return 2;
        var requestPath=Required("--request");if(!Path.IsPathFullyQualified(requestPath)||new FileInfo(requestPath).Length>65536)throw new ArgumentException("安装请求文件无效");using var request=JsonDocument.Parse(File.ReadAllText(requestPath));var root=request.RootElement;
        if(root.GetProperty("productId").GetString()!=ProductTrust.ProductId)throw new ArgumentException("安装请求不属于本产品");var installation=root.GetProperty("installationId").GetString()??throw new ArgumentException("缺少安装编号");
        var permanent=args.Contains("--permanent");var expires=Option("--expires");if(permanent==(expires is not null))throw new ArgumentException("请明确选择--permanent，或通过--expires指定含时区的到期时刻，软件不代选天数");
        DateTimeOffset? expiry=null;if(expires is not null){if(!DateTimeOffset.TryParseExact(expires,["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:sszzz"],CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed))throw new ArgumentException("到期时间应含时区，例如2026-11-30T16:00:00Z");expiry=parsed.ToUniversalTime();}
        var branding=Option("--branding") is {} brandPath?JsonSerializer.Deserialize<ProductBranding>(File.ReadAllText(brandPath),LicenseVerifier.Json)??throw new ArgumentException("品牌资料为空"):new();
        var license=new ProductLicense{LicenseId=Guid.NewGuid(),InstallationId=installation,CustomerName=Required("--customer"),Sequence=long.Parse(Required("--sequence"),CultureInfo.InvariantCulture),IssuedAtUtc=DateTimeOffset.UtcNow,ExpiresAtUtc=expiry,RepairClockGuard=args.Contains("--repair-clock"),EnabledModules=(args.Contains("--average-temperature")?new[]{ProductTrust.AverageModule}:Array.Empty<string>()).Concat(ProductTrust.OptionalOperations.Where(p=>args.Contains("--"+p.Value)).Select(p=>p.Value)).ToArray(),Branding=branding};
        var privatePath=Required("--private-key");if(!Path.IsPathFullyQualified(privatePath)||new FileInfo(privatePath).Length>16384)throw new ArgumentException("供方私钥文件无效");using var signer=ECDsa.Create();signer.ImportFromPem(File.ReadAllText(privatePath));var payload=JsonSerializer.SerializeToUtf8Bytes(license,LicenseVerifier.Json);var signature=signer.SignData(payload,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);var envelope=JsonSerializer.Serialize(new LicenseEnvelope(Convert.ToBase64String(payload),Convert.ToBase64String(signature)),LicenseVerifier.Json);
        _=LicenseVerifier.Verify(envelope,installation,ProductTrust.PublicKeyPem);NewFile(Required("--output"),envelope);
        Console.WriteLine("本产品离线授权已签发："+license.CustomerName+"，序号 "+license.Sequence+"，"+(expiry?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz")??"永久"));Console.WriteLine("仅将授权文件交给客户；本工具和私钥留供方。");return 0;
    }
}
