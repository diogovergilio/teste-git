# Coletor

Serviço leve que roda **dentro da VPS**, lê o banco local em **modo somente leitura** e envia os
dados por uma **conexão de saída** (WebSocket sobre HTTPS) para um servidor central. Não abre
nenhuma porta de entrada.

Distribuído em repositório público apenas para simplificar o deploy (`git clone` sem autenticação).
Sem segredos no código — toda a configuração vem por variáveis de ambiente (`.env`).

## Deploy

Pré-requisitos na VPS: **Docker** e um usuário de banco **somente leitura**.

```bash
git clone https://github.com/diogovergilio/teste-git.git /opt/coletor
cd /opt/coletor
cp .env.example .env       # preencha tenant, senha do usuário read-only, URL do servidor e token
docker compose --env-file .env up -d --build
docker compose logs -f
```

Esperado no log: `[collector] tenant=... -> wss://.../collector/stream` e sem "conexão caiu".

## Atualizar

```bash
cd /opt/coletor && git pull && docker compose --env-file .env up -d --build
```
