#!/bin/bash
# Instala o Jitsi Meet (reuniões por vídeo) no softphone-server, atrás do nginx do Hub, com login por JWT emitido pelo Hub
# (só entra quem está logado no Softphone). Rodar como root depois de extrair o pacote:
#   bash /opt/softphone/jitsi/instalar-jitsi.sh
set -euo pipefail
BASE=/opt/softphone/jitsi
CFG=/opt/softphone/jitsi-cfg
HUB=/opt/softphone/hub
URL=https://meet.corp.example.com
IP=192.0.2.5

erro() { echo "ERRO: $*" >&2; exit 1; }
[[ $EUID -eq 0 ]] || erro "rode como root."
command -v docker >/dev/null || erro "Docker não instalado."
[[ -f $HUB/hub.env ]] || erro "instale o Hub antes ($HUB/hub.env não existe)."
cd "$BASE"

# 1) docker-jitsi-meet oficial (última versão estável), baixado uma vez
if [[ ! -f docker-compose.yml ]]; then
  TAG=$(curl -fsSL https://api.github.com/repos/jitsi/docker-jitsi-meet/releases/latest | grep -m1 '"tag_name"' | cut -d'"' -f4)
  [[ -n "$TAG" ]] || erro "não consegui descobrir a versão do Jitsi (sem internet?)."
  echo "Baixando docker-jitsi-meet $TAG..."
  curl -fsSL "https://github.com/jitsi/docker-jitsi-meet/archive/refs/tags/$TAG.tar.gz" | tar xz --strip-components=1
  echo "$TAG" > VERSAO
fi
echo "Jitsi: $(cat VERSAO 2>/dev/null)"

# 2) .env com senhas internas geradas + configuração da empresa
if [[ ! -f .env ]]; then cp env.example .env; bash gen-passwords.sh; fi
# segredo do JWT: o mesmo no Jitsi e no Hub (reaproveita se já existe)
SEGREDO=$(grep -m1 '^Hub__Jitsi__Segredo=' "$HUB/hub.env" | cut -d= -f2- || true)
[[ ${#SEGREDO} -ge 32 ]] || SEGREDO=$(openssl rand -hex 32)

definir() { # definir CHAVE VALOR (troca a linha, comentada ou não, ou acrescenta)
  if grep -qE "^#?[[:space:]]*$1=" .env; then sed -i -E "s|^#?[[:space:]]*$1=.*|$1=$2|" .env; else echo "$1=$2" >> .env; fi
}
definir CONFIG "$CFG"
definir TZ America/Sao_Paulo
definir PUBLIC_URL "$URL"
definir HTTP_PORT 127.0.0.1:8000        # só local: quem atende de fora é o nginx do Hub (443)
definir HTTPS_PORT 127.0.0.1:8443
definir DISABLE_HTTPS 1
definir ENABLE_HTTP_REDIRECT 0
definir ENABLE_LETSENCRYPT 0
definir JVB_ADVERTISE_IPS "$IP"           # áudio/vídeo: UDP 10000 direto no servidor
definir ENABLE_AUTH 1
definir AUTH_TYPE jwt
definir JWT_APP_ID softphone
definir JWT_APP_SECRET "$SEGREDO"
definir JWT_ACCEPTED_ISSUERS softphone
definir JWT_ACCEPTED_AUDIENCES jitsi
definir ENABLE_GUESTS 0
definir ENABLE_PREJOIN_PAGE 0
definir ENABLE_WELCOME_PAGE 0
definir DEFAULT_LANGUAGE ptBR
chmod 600 .env

mkdir -p "$CFG"/{web,transcripts,prosody/config,prosody/prosody-plugins-custom,jicofo,jvb,jigasi,jibri,storage/prosody}
# as imagens novas do Jitsi rodam como uid 1000 e precisam gravar aqui (senão o prosody fica reiniciando)
chown -R 1000:1000 "$CFG"

# 3) sobe o Jitsi (projeto "jitsi" => rede jitsi_meet.jitsi)
docker compose -p jitsi up -d

# 4) Hub: endereço + segredo das reuniões, e o nginx do Hub entra na rede do Jitsi
grep -q '^Hub__Jitsi__Url=' "$HUB/hub.env" || echo "Hub__Jitsi__Url=$URL" >> "$HUB/hub.env"
grep -q '^Hub__Jitsi__Segredo=' "$HUB/hub.env" || echo "Hub__Jitsi__Segredo=$SEGREDO" >> "$HUB/hub.env"
chmod 600 "$HUB/hub.env"
cat > "$HUB/docker-compose.override.yml" <<'YAML'
services:
  proxy:
    networks: [default, jitsi]
networks:
  jitsi:
    external: true
    name: jitsi_meet.jitsi
YAML
cd "$HUB" && docker compose up -d --build

# 5) conferência
echo "Aguardando o Jitsi..."
for i in $(seq 1 40); do
  if curl -fsS -o /dev/null --cacert /opt/softphone/certs/corp-root-ca.crt --resolve meet.corp.example.com:443:127.0.0.1 "$URL/"; then
    echo "OK: reuniões no ar em $URL"
    curl -fsS --cacert /opt/softphone/certs/corp-root-ca.crt --resolve softphone.corp.example.com:443:127.0.0.1 https://softphone.corp.example.com/saude; echo
    exit 0
  fi
  sleep 3
done
erro "o Jitsi não respondeu. Veja: docker compose -p jitsi -f $BASE/docker-compose.yml logs --tail 50"
