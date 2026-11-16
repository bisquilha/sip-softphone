#!/bin/bash
# Instala/atualiza o Hub do Softphone no softphone-server (Debian + Docker). Rodar como root, dentro da pasta hub:
#   cd /opt/softphone/hub && bash instalar-hub.sh
set -euo pipefail
cd "$(dirname "$0")"
CERTS=/opt/softphone/certs

[[ $EUID -eq 0 ]] || { echo "Rode como root."; exit 1; }
[[ -f $CERTS/softphone.crt && -f $CERTS/softphone.key && -f $CERTS/corp-root-ca.crt ]] || { echo "Faltam os certificados em $CERTS."; exit 1; }
if [[ ! -f hub.env ]]; then
  cp hub.env.exemplo hub.env && chmod 600 hub.env
  echo "Criei o hub.env: coloque a senha da conta svc-softphone (nano hub.env) e rode de novo."; exit 1
fi
chmod 600 hub.env
grep -q '^Hub__Ldap__Senha=TROCAR$' hub.env && { echo "Troque Hub__Ldap__Senha no hub.env (nano hub.env)."; exit 1; }

mkdir -p /opt/softphone/dados /opt/softphone/kerberos
chown -R 1654:1654 /opt/softphone/dados              # usuário 'app' do contêiner
KT=/opt/softphone/kerberos/softphone.keytab
if [[ ! -s $KT ]]; then
  echo "AVISO: sem keytab em $KT: o login automático pelo Windows fica desligado (o app pede usuário/senha uma vez)."
  : > "$KT"
fi
chown 1654:1654 "$KT" && chmod 400 "$KT"

cp "$CERTS/corp-root-ca.crt" ./corp-root-ca.crt   # vai para dentro da imagem (LDAPS)
docker compose up -d --build
echo "Aguardando o Hub subir..."
for i in $(seq 1 30); do
  if curl -fsS --cacert "$CERTS/corp-root-ca.crt" --resolve softphone.corp.example.com:443:127.0.0.1 https://softphone.corp.example.com/saude 2>/dev/null; then
    echo; echo "OK: Hub no ar em https://softphone.corp.example.com"; exit 0
  fi
  sleep 2
done
echo "O Hub não respondeu. Veja: docker compose logs --tail 50 hub"; exit 1
