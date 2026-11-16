#!/bin/bash
# Atualiza o Hub do Softphone no softphone-server depois de extrair o pacote novo em /opt/softphone:
#   cd /opt/softphone && tar xzf softphone-hub.tgz && bash hub/atualizar-hub.sh
# - guarda uma cópia do hub.env e acrescenta só as linhas novas que faltarem (não mexe nas que já existem);
# - para o Hub e guarda o banco e as chaves em /opt/softphone/bak-<data> (cópia consistente, com o SQLite fechado);
# - sobe o Hub novo + o whisper (transcrição local); o Jitsi não é tocado;
# - confere que o Hub respondeu e que a versão é a do pacote (arquivo VERSAO); se não, sai com erro e mostra o log.
# A chave da API da Anthropic NÃO é colocada aqui: vai pelo painel web https://softphone.corp.example.com/admin/
# (aba IA), criptografada no servidor. A linha Hub__Ia__AnthropicChave= do hub.env continua valendo como reserva.
# (Claude Code, 05/11/2026: conferência da versão, saída com erro e cópia do banco — achado R13 da revisão do Codex.)
set -euo pipefail
cd /opt/softphone/hub
ENV=hub.env
[ -f "$ENV" ] || { echo "Falta $PWD/$ENV (primeira instalação? use instalar-hub.sh)"; exit 1; }
ESPERADA=$(tr -d '[:space:]' < VERSAO 2>/dev/null || true)
DATA=$(date +%Y%m%d-%H%M%S)

cp -a "$ENV" "$ENV.bak-$DATA"
acrescentar() { grep -q "^$1=" "$ENV" || { echo "$1=$2" >> "$ENV"; echo "  + $1"; }; }
grep -q '^Hub__Ia__' "$ENV" || printf '\n# Gravação de ligações + IA (transcrição local; resumo pela API da Anthropic)\n' >> "$ENV"
echo "hub.env (cópia guardada; linhas acrescentadas):"
acrescentar Hub__Ia__WhisperUrl http://whisper:9000
acrescentar Hub__Ia__RetencaoDias 90
acrescentar Hub__Ia__AnthropicChave ""
acrescentar Hub__Admins__0 ana.souza
chmod 600 "$ENV"

# Banco e chaves: copiados com o Hub parado (o SQLite aberto pode não estar consistente)
BAK=/opt/softphone/bak-$DATA
echo "Parando o Hub e guardando banco e chaves em $BAK ..."
docker compose stop hub >/dev/null 2>&1 || true
mkdir -p "$BAK"
for f in hub.db hub.db-wal hub.db-shm jwt.key; do
  if [ -f "/opt/softphone/dados/$f" ]; then cp -a "/opt/softphone/dados/$f" "$BAK/"; fi
done
if [ -d /opt/softphone/dados/chaves-dp ]; then cp -a /opt/softphone/dados/chaves-dp "$BAK/"; fi
echo "  guardado: $(ls "$BAK" | tr '\n' ' ')"

mkdir -p /opt/softphone/whisper-cache /opt/softphone/dados/conhecimento
[ -f /opt/softphone/dados/conhecimento/MODELO.md.exemplo ] || cp ../conhecimento/MODELO.md.exemplo /opt/softphone/dados/conhecimento/ 2>/dev/null || true
chown -R 1654:1654 /opt/softphone/dados 2>/dev/null || true   # usuário "app" do contêiner do .NET

echo "Baixando imagens (o whisper tem ~2 GB na primeira vez)..."
docker compose pull whisper proxy
docker compose up -d --build

echo "Aguardando o Hub subir..."
SAUDE=$(mktemp)
trap 'rm -f "$SAUDE"' EXIT
OK=0
for i in $(seq 1 30); do
  if curl -fsk --max-time 5 https://localhost/saude -H 'Host: softphone.corp.example.com' -o "$SAUDE" 2>/dev/null && grep -q '"versao"' "$SAUDE"; then
    OK=1; break
  fi
  sleep 2
done
docker compose ps
echo
if [ "$OK" != 1 ]; then
  echo "ERRO: o Hub não respondeu em 60 s. Últimas linhas do log do Hub:"
  docker compose logs --tail 40 hub || true
  echo
  echo "Banco e chaves de antes da atualização estão em $BAK."
  exit 1
fi
cat "$SAUDE"; echo; echo
VERSAO=$(sed -n 's/.*"versao":"\([^"]*\)".*/\1/p' "$SAUDE")
if [ -n "$ESPERADA" ] && [ "$VERSAO" != "$ESPERADA" ]; then
  echo "ERRO: o Hub respondeu a versão '$VERSAO', mas este pacote é a $ESPERADA. Veja: docker compose logs --tail 40 hub"
  exit 1
fi
echo "Pronto: Hub $VERSAO no ar. Confira \"transcricao\":true acima."
echo "Banco e chaves de antes da atualização: $BAK"
echo "Chave da IA e demais ajustes: https://softphone.corp.example.com/admin/"
