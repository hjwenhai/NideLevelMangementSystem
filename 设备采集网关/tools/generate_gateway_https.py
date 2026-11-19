"""Generate a private CA and gateway HTTPS identity. Does not install trust or touch a gateway."""
from pathlib import Path
from datetime import datetime,timezone
import argparse,ipaddress,json,os
from cryptography import x509
from cryptography.hazmat.primitives import hashes,serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID,ExtendedKeyUsageOID

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output',required=True)
args=parser.parse_args()
dest=Path(args.output).resolve()
if dest.exists() and any(dest.iterdir()):parser.error('Output must be a new or empty directory; existing certificates are never overwritten.')
dest.mkdir(parents=True,exist_ok=True,mode=0o700)
for folder in ['device','client','manufacturer-private']:(dest/folder).mkdir(mode=0o700)
def save(name,body):
 with os.fdopen(os.open(dest/name,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'wb') as f:f.write(body)
utc=timezone.utc
begin=datetime(2026,10,18,0,0,tzinfo=utc)
server_end=datetime(2027,10,19,15,59,59,tzinfo=utc)
ca_end=datetime(2036,10,19,15,59,59,tzinfo=utc)
ca_key=rsa.generate_private_key(public_exponent=65537,key_size=3072)
ca_name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'GT6757X Project Local Root CA 20261019')])
ca=(x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name).public_key(ca_key.public_key())
 .serial_number(x509.random_serial_number()).not_valid_before(begin).not_valid_after(ca_end)
 .add_extension(x509.BasicConstraints(ca=True,path_length=0),critical=True)
 .add_extension(x509.KeyUsage(digital_signature=False,content_commitment=False,key_encipherment=False,data_encipherment=False,key_agreement=False,key_cert_sign=True,crl_sign=True,encipher_only=False,decipher_only=False),critical=True)
 .add_extension(x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key()),critical=False)
 .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()),critical=False)
 .sign(ca_key,hashes.SHA256()))
server_key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
ips=['192.168.0.230','192.168.1.230']
server_name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'GT6757S Gateway')])
server=(x509.CertificateBuilder().subject_name(server_name).issuer_name(ca.subject).public_key(server_key.public_key())
 .serial_number(x509.random_serial_number()).not_valid_before(begin).not_valid_after(server_end)
 .add_extension(x509.BasicConstraints(ca=False,path_length=None),critical=True)
 .add_extension(x509.KeyUsage(digital_signature=True,content_commitment=False,key_encipherment=True,data_encipherment=False,key_agreement=False,key_cert_sign=False,crl_sign=False,encipher_only=False,decipher_only=False),critical=True)
 .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]),critical=False)
 .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address(ip)) for ip in ips]),critical=False)
 .add_extension(x509.SubjectKeyIdentifier.from_public_key(server_key.public_key()),critical=False)
 .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()),critical=False)
 .sign(ca_key,hashes.SHA256()))
save('device/server.crt',server.public_bytes(serialization.Encoding.PEM))
save('device/server.key',server_key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
save('client/GT6757X-Local-Root-CA.crt',ca.public_bytes(serialization.Encoding.PEM))
save('client/GT6757X-Local-Root-CA.cer',ca.public_bytes(serialization.Encoding.DER))
save('manufacturer-private/ca.key',ca_key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
manifest={'created_date':'2026-10-19','timezone':'Asia/Shanghai','kind':'private CA signed gateway identity','ip_sans':ips,
 'server_expires_local':'2027-10-19 23:59:59 +08:00','root_expires_local':'2036-10-19 23:59:59 +08:00',
 'server_sha256':server.fingerprint(hashes.SHA256()).hex().upper(),'root_sha256':ca.fingerprint(hashes.SHA256()).hex().upper(),
 'device_files':['device/server.crt','device/server.key'],'client_trust_certificate':'client/GT6757X-Local-Root-CA.cer',
 'private_ca_file':'manufacturer-private/ca.key','trust_installed':False,'deployed_to_gateway':False,'private_keys_encrypted':False,
 'default_web_transport':'http','https_enabled':False,
 'key_protection':'Local folder access restricted to current Windows account, SYSTEM and Administrators; restrict server.key to root mode0600 on the gateway.'}
save('manifest.json',(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n').encode())
print(json.dumps({'created':str(dest),'ip_sans':ips,'server_expires_local':manifest['server_expires_local'],'root_sha256':manifest['root_sha256']},ensure_ascii=False))
