# PaperTodo 独立同步服务

Go 单文件程序，无需 Docker 或额外运行库。WebDAV / S3 用户不需要部署本服务。

## 启动

服务默认只监听 `127.0.0.1:8080`，必须设置非空 Token。使用足够长的随机 Token，推荐通过 `PAPERTODO_TOKEN` 环境变量或受限权限的服务配置提供，避免命令行进程列表暴露。

```powershell
# Windows：先在当前进程环境中设置 PAPERTODO_TOKEN
.\papertodo-server.exe -host=127.0.0.1 -port=8080 -data-dir=.\data
```

```sh
# Linux：先设置 PAPERTODO_TOKEN；下载后的二进制需赋予执行权限
chmod +x ./papertodo-server
./papertodo-server -host=127.0.0.1 -port=8080 -data-dir=./data
```

通过支持 HTTPS 的反向代理暴露服务，再把 HTTPS 地址和相同 Token 填入两端设置。需要显式监听其他地址时使用 `-host`；程序自身不终止 TLS，不应直接把带 Token 的明文 HTTP 暴露到公网。目录应仅允许服务账户访问，并纳入独立备份。

## 协议与备份

- `/api/v1/ping`、`/api/v1/sync`、`/api/v1/backup/export` 都校验 Token；连接测试同时检查数据目录可写。
- 同步正文仅保留 `schemaVersion/papers/syncTombstones`，也过滤旧客户端上传的顶层设置和凭据。历史备份不会被自动重写。
- GET 返回 ETag；新客户端上传携带 `If-Match` 或 `If-None-Match`。冲突返回 412，客户端重新拉取合并。旧客户端的无条件写入仅为兼容，建议先升级两端。
- 无效文档、超过 32 MB 的请求、备份失败和过期写入不会替换现有数据。
- 写入采用临时文件与替换，写前保留独立时间戳备份，保留最近 30 份。恢复前停止服务，复制所需备份为 `data.json`，保留当前文件以便回退。

## 构建与测试

```sh
go test ./...
CGO_ENABLED=0 go build -trimpath -ldflags='-s -w' -o papertodo-server .
```

测试覆盖鉴权、无效正文、凭据过滤、过期 ETag 和连续写入备份。Windows 客户端还有真实 HTTP 联调测试。

Docker 文件只作为可选部署方式；默认端口映射仍限制本机，必须自行提供 `PAPERTODO_TOKEN` 并配置 HTTPS 入口。
