# Softphone Hub (chat, presença, diretório). A imagem usa o app já publicado em ./app (Publicar-Hub.ps1 no PC da TI).
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Kerberos (login automático pelo Windows) + curl para o healthcheck
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 libkrb5-3 ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

# Confia na CA da empresa (LDAPS do AD)
COPY corp-root-ca.crt /usr/local/share/ca-certificates/corp-root-ca.crt
RUN update-ca-certificates \
 && mkdir -p /etc/ldap \
 && printf 'TLS_CACERT /etc/ssl/certs/ca-certificates.crt\nTLS_REQCERT demand\n' > /etc/ldap/ldap.conf
# (sem esse ldap.conf o OpenLDAP não acha as CAs e o LDAPS falha como "The LDAP server is unavailable")

WORKDIR /app
COPY app/ .
ENV ASPNETCORE_URLS=http://+:8080 \
    Hub__Dados=/dados \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fs http://localhost:8080/saude || exit 1
# usuário sem privilégios que já vem na imagem da Microsoft (uid 1654)
USER app
ENTRYPOINT ["dotnet", "Softphone.Hub.dll"]
