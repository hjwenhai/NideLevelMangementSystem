"""Validate certificates and the compiled Go HTTPS server on loopback; do not install OS trust."""
from pathlib import Path
from datetime import datetime,timezone
import hashlib,http.client,ipaddress,json,socket,ssl,subprocess,tempfile,time
from cryptography import x509
from cryptography.hazmat.primitives import serialization,hashes
from cryptography.x509.oid import ExtendedKeyUsageOID
ROOT=Path(__file__).resolve().parents[1]
bundle=ROOT/'output/https/GT6757S-20261019-r1'
work=Path(tempfile.mkdtemp(prefix='https-',dir=ROOT/'tmp'))
cert=x509.load_pem_x509_certificate((bundle/'device/server.crt').read_bytes())
ca=x509.load_pem_x509_certificate((bundle/'client/GT6757X-Local-Root-CA.crt').read_bytes())
key=serialization.load_pem_private_key((bundle/'device/server.key').read_bytes(),password=None)
manifest=json.loads((bundle/'manifest.json').read_text(encoding='utf-8'))
checks=[]
def check(name,condition):
 assert condition,name
 checks.append({'name':name,'passed':True})
cert.verify_directly_issued_by(ca);ca.verify_directly_issued_by(ca)
check('root_and_server_signatures_valid',True)
pub=lambda k:k.public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)
check('server_key_matches_certificate',pub(key.public_key())==pub(cert.public_key()))
check('root_DER_matches_PEM',x509.load_der_x509_certificate((bundle/'client/GT6757X-Local-Root-CA.cer').read_bytes()).fingerprint(hashes.SHA256())==ca.fingerprint(hashes.SHA256()))
check('IP_SANs_match_two_confirmed_addresses',set(map(str,cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value.get_values_for_type(x509.IPAddress)))==set(manifest['ip_sans']))
check('constraints_and_server_auth',not cert.extensions.get_extension_for_class(x509.BasicConstraints).value.ca and ca.extensions.get_extension_for_class(x509.BasicConstraints).value.path_length==0 and ExtendedKeyUsageOID.SERVER_AUTH in cert.extensions.get_extension_for_class(x509.ExtendedKeyUsage).value)
now=datetime.now(timezone.utc)
check('valid_now_and_root_outlives_server',ca.not_valid_before_utc<=now<=cert.not_valid_after_utc<ca.not_valid_after_utc and cert.not_valid_before_utc<=now)
check('manifest_fingerprints_match',manifest['server_sha256']==cert.fingerprint(hashes.SHA256()).hex().upper() and manifest['root_sha256']==ca.fingerprint(hashes.SHA256()).hex().upper())
binary=ROOT/'output/windows-amd64/gateway-web.exe'
auth=work/'admin.json';password='HTTPS-Local-Test-2026!'
flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
subprocess.run([str(binary),'--init-admin','--auth-file',str(auth)],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=flags)
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
log=(work/'web.log').open('wb')
p=subprocess.Popen([str(binary),'--listen',f'127.0.0.1:{port}','--ipc','127.0.0.1:19090','--token-file',str(work/'token'),'--auth-file',str(auth),'--tls-cert',str(bundle/'device/server.crt'),'--tls-key',str(bundle/'device/server.key')],stdout=log,stderr=log,creationflags=flags)
ctx=ssl.create_default_context(cafile=str(bundle/'client/GT6757X-Local-Root-CA.crt'))
def connect(context,hostname):
 raw=socket.create_connection(('127.0.0.1',port),timeout=3)
 try:return context.wrap_socket(raw,server_hostname=hostname)
 except BaseException:raw.close();raise
try:
 end=time.monotonic()+10
 while True:
  try:
   with connect(ctx,manifest['ip_sans'][0]):break
  except ConnectionRefusedError:
   if time.monotonic()>=end:raise
   time.sleep(.05)
 for host in manifest['ip_sans']:
  with connect(ctx,host) as s:
   s.sendall(f'GET / HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n'.encode())
   response=http.client.HTTPResponse(s);response.begin();body=response.read()
   check('compiled_HTTPS_server_verified_for_'+host,response.status==200 and '管理员登录'.encode() in body)
 try:
  with connect(ctx,'192.168.0.231'):pass
  raise AssertionError('Wrong IP accepted')
 except ssl.SSLCertVerificationError:check('wrong_IP_rejected',True)
 try:
  with connect(ssl.create_default_context(),manifest['ip_sans'][0]):pass
  raise AssertionError('Private root unexpectedly trusted by system')
 except ssl.SSLCertVerificationError:check('requires_explicit_private_root_trust',True)
 host=manifest['ip_sans'][0];body=json.dumps({'password':password}).encode()
 with connect(ctx,host) as s:
  header=f'POST /api/login HTTP/1.1\r\nHost: {host}:{port}\r\nOrigin: https://{host}:{port}\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n'.encode()
  s.sendall(header+body);response=http.client.HTTPResponse(s);response.begin();data=json.loads(response.read())
  check('HTTPS_login_and_secure_cookie',response.status==200 and bool(data.get('csrf')) and 'Secure' in response.getheader('Set-Cookie',''))
finally:
 p.terminate();p.wait(timeout=10);log.close()
result={'date':'2026-10-19','passed':True,'checks':checks,'compiled_web_sha256':hashlib.sha256(binary.read_bytes()).hexdigest(),
 'certificate_manifest':str((bundle/'manifest.json').relative_to(ROOT)),'trust_installed':False,'hardware_accessed':False,'host_clock_modified':False,
 'scope':'Local certificate validation and actual compiled Go HTTPS/login; no gateway deployment or browser/OS trust installation.'}
(ROOT/'tests/https-certificate-verification-20261019.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks)},ensure_ascii=False))
