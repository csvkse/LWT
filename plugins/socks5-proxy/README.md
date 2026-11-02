# SOCKS5 安全代理微服务 (socks5-proxy)

基于 Go 原生运行引擎（`runtime: go`）构建的轻量极速 SOCKS5 网络代理微服务，完全遵循 RFC 1928 与 RFC 1929 规范。

## 功能特性

- **RFC 1928 完整支持**：支持 No-Auth (`0x00`)、用户名密码认证 (`0x02`)；
- **多地址族支持**：支持 IPv4、IPv6 与 FQDN 域名解析（防本地 DNS 污染与泄漏）；
- **零外部依赖**：纯 Go 标准库实现，启动零网络开销，离线毫秒级编译与调度；
- **JIT 增量哈希构建缓存**：源码未变更时启动耗时 < 5ms，杜绝 `go run` 孤儿进程隐患；
- **平台生命周期监管**：集成端口租约自动分配与回收、崩溃重试退避与 Web 实时日志。

## 启动参数

- `--listen <host:port>`：监听地址与端口（默认使用系统动态分配端口，如 `0.0.0.0:18001`）；
- `--auth-mode <none|password>`：认证模式（`none` 或 `password`）；
- `--username <string>`：认证用户名（亦可通过环境变量 `SOCKS5_USERNAME` 注入）；
- `--password <string>`：认证密码（亦可通过环境变量 `SOCKS5_PASSWORD` 注入）；
- `--connect-timeout <seconds>`：向上游建立连接的超时秒数（默认 10s）。
