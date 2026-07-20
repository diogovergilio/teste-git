# Coletor

Serviço leve que roda **dentro da VPS**, lê o banco local em **modo somente leitura** e envia os
dados por uma **conexão de saída** (WebSocket sobre HTTPS) para um servidor central. Não abre
nenhuma porta de entrada.

Distribuído em repositório público apenas para simplificar o deploy (`git clone` sem autenticação).
Sem segredos no código — toda a configuração vem por variáveis de ambiente (`.env`).

## Deploy (imagem pronta — a VPS não baixa o SDK)

Pré-requisitos na VPS: **Docker** e um usuário de banco **somente leitura**.

### Permissões do usuário read-only

```sql
GRANT SELECT ON call_center.*           TO 'dashcall_ro'@'localhost';
GRANT SELECT ON asterisk.queues_config  TO 'dashcall_ro'@'localhost';
GRANT SELECT ON asterisk.queues_details TO 'dashcall_ro'@'localhost';
-- Opcional: pesquisa de satisfação (banco separado). Sem este GRANT, o relatório
-- continua saindo — apenas sem o bloco de satisfação.
GRANT SELECT ON pesquisa.*              TO 'dashcall_ro'@'localhost';
FLUSH PRIVILEGES;
```

> `localhost` e `127.0.0.1` são hosts DIFERENTES para o MySQL (socket vs TCP). O coletor conecta por
> TCP — teste do mesmo jeito que ele: `mysql -h 127.0.0.1 -P 3306 -u dashcall_ro -p`.

A imagem é construída em outra máquina (dev/CI) e a VPS só carrega a imagem de runtime
(~200 MB) — economiza ~1 GB (o SDK .NET nunca é baixado na VPS).

**Na máquina de build (dev/CI):**
```bash
git clone https://github.com/diogovergilio/teste-git.git && cd teste-git
docker build -t dashcall-collector:latest .
docker save dashcall-collector:latest | gzip > col.tar.gz
scp col.tar.gz root@VPS:/opt/coletor/
```

**Na VPS:**
```bash
cd /opt/coletor
git clone https://github.com/diogovergilio/teste-git.git .   # (1ª vez) traz compose + .env.example
cp .env.example .env       # preencha tenant, senha do read-only, URL do hub e token
gunzip -c col.tar.gz | docker load
docker compose --env-file .env up -d       # SEM --build (usa a imagem carregada)
docker compose logs -f
```
Esperado no log: `[collector] tenant=... -> wss://.../collector/stream` e sem "conexão caiu".

## Atualizar

Rebuild na máquina de dev → `docker save | gzip` → `scp` → na VPS `docker load` + `docker compose up -d`.
(Se a VPS tiver espaço de sobra, dá para `git pull` + `docker build ... && docker compose up -d` nela mesma.)
