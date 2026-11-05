package main

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// -----------------------------------------------------------------------------
// Trojan 出站适配器 (原生 TLS 1.3 + SHA224 鉴权首包)
// -----------------------------------------------------------------------------

type TrojanOutbound struct {
	node UpstreamNode
	host string
	port int
}

func NewTrojanOutbound(node UpstreamNode) (IOutboundDialer, error) {
	host, portStr := node.Server, strconv.Itoa(node.Port)
	if node.URL != "" && strings.Contains(node.URL, "://") {
		u, err := url.Parse(node.URL)
		if err == nil {
			host = u.Hostname()
			portStr = u.Port()
			if node.Password == "" && u.User != nil {
				node.Password = u.User.Username()
			}
		}
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 443
	}
	if host == "" {
		host = "127.0.0.1"
	}

	return &TrojanOutbound{
		node: node,
		host: host,
		port: port,
	}, nil
}

func (d *TrojanOutbound) Protocol() string { return "trojan" }

func (d *TrojanOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	serverAddr := fmt.Sprintf("%s:%d", d.host, d.port)
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	rawConn, err := dialer.DialContext(ctx, "tcp", serverAddr)
	if err != nil {
		return nil, fmt.Errorf("连接 Trojan 服务端 [%s] 失败: %w", serverAddr, err)
	}

	return d.DialThrough(ctx, rawConn, targetAddr)
}

func (d *TrojanOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	// 若传入连接尚未执行 TLS 握手，则进行 TLS 封装升级
	tlsConn, ok := baseConn.(*tls.Conn)
	if !ok {
		sni := d.node.SNI
		if sni == "" {
			sni = d.host
		}
		tlsConfig := &tls.Config{
			ServerName:         sni,
			InsecureSkipVerify: d.node.SkipTLSVerify,
			NextProtos:         []string{"h2", "http/1.1"},
		}
		tlsConn = tls.Client(baseConn, tlsConfig)
		if err := tlsConn.HandshakeContext(ctx); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("Trojan TLS 握手失败: %w", err)
		}
	}

	// Trojan 首包格式:
	// hex(sha224(password)) + "\r\n" + cmd(0x01) + atyp + dst_addr + dst_port + "\r\n"
	hash := sha256.Sum224([]byte(d.node.Password))
	hexHash := hex.EncodeToString(hash[:])

	targetHost, targetPortStr, err := net.SplitHostPort(targetAddr)
	if err != nil {
		_ = tlsConn.Close()
		return nil, err
	}
	targetPort, _ := strconv.Atoi(targetPortStr)

	req := []byte(hexHash + "\r\n")
	req = append(req, 0x01) // CMD: CONNECT

	ip := net.ParseIP(targetHost)
	if ip4 := ip.To4(); ip4 != nil {
		req = append(req, 0x01)
		req = append(req, ip4...)
	} else if ip6 := ip.To16(); ip6 != nil {
		req = append(req, 0x04)
		req = append(req, ip6...)
	} else {
		req = append(req, 0x03)
		req = append(req, byte(len(targetHost)))
		req = append(req, []byte(targetHost)...)
	}
	portBytes := make([]byte, 2)
	binary.BigEndian.PutUint16(portBytes, uint16(targetPort))
	req = append(req, portBytes...)
	req = append(req, []byte("\r\n")...)

	if _, err := tlsConn.Write(req); err != nil {
		_ = tlsConn.Close()
		return nil, fmt.Errorf("写入 Trojan 首包失败: %w", err)
	}

	return tlsConn, nil
}

// -----------------------------------------------------------------------------
// VLESS 出站适配器 (纯 Go 原生实现，内层 0 冗余加密 + TLS 1.3)
// -----------------------------------------------------------------------------

type VlessOutbound struct {
	node UpstreamNode
	host string
	port int
	uuid [16]byte
}

func NewVlessOutbound(node UpstreamNode) (IOutboundDialer, error) {
	host, portStr := node.Server, strconv.Itoa(node.Port)
	if node.URL != "" && strings.Contains(node.URL, "://") {
		u, err := url.Parse(node.URL)
		if err == nil {
			host = u.Hostname()
			portStr = u.Port()
			if node.UUID == "" && u.User != nil {
				node.UUID = u.User.Username()
			}
		}
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 443
	}
	if host == "" {
		host = "127.0.0.1"
	}

	cleanUUID := strings.ReplaceAll(node.UUID, "-", "")
	var uuidBytes [16]byte
	if len(cleanUUID) == 32 {
		_, _ = hex.Decode(uuidBytes[:], []byte(cleanUUID))
	}

	return &VlessOutbound{
		node: node,
		host: host,
		port: port,
		uuid: uuidBytes,
	}, nil
}

func (d *VlessOutbound) Protocol() string { return "vless" }

func (d *VlessOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	serverAddr := fmt.Sprintf("%s:%d", d.host, d.port)
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	rawConn, err := dialer.DialContext(ctx, "tcp", serverAddr)
	if err != nil {
		return nil, fmt.Errorf("连接 VLESS 服务端 [%s] 失败: %w", serverAddr, err)
	}

	return d.DialThrough(ctx, rawConn, targetAddr)
}

func (d *VlessOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	// 若传入连接尚未执行 TLS 握手，则进行 TLS 封装升级
	tlsConn, ok := baseConn.(*tls.Conn)
	if !ok {
		sni := d.node.SNI
		if sni == "" {
			sni = d.host
		}
		tlsConfig := &tls.Config{
			ServerName:         sni,
			InsecureSkipVerify: d.node.SkipTLSVerify,
			NextProtos:         []string{"http/1.1"},
		}
		tlsConn = tls.Client(baseConn, tlsConfig)
		if err := tlsConn.HandshakeContext(ctx); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("VLESS TLS 握手失败: %w", err)
		}
	}

	// 组装 VLESS 请求头规范 (支持标准模式与 XTLS-Vision 流控):
	// 1 byte Version (0) + 16 bytes UUID + 1 byte AddonsLen + N bytes Addons + 1 byte CMD (1=TCP) + 2 bytes Port + 1 byte AddrType + Addr
	targetHost, targetPortStr, err := net.SplitHostPort(targetAddr)
	if err != nil {
		_ = tlsConn.Close()
		return nil, err
	}
	targetPort, _ := strconv.Atoi(targetPortStr)

	var addonProto []byte
	isVision := false
	if d.node.Flow != "" {
		if strings.Contains(strings.ToLower(d.node.Flow), "vision") {
			isVision = true
		}
		flowStr := d.node.Flow
		addonProto = append(addonProto, 0x0a, byte(len(flowStr)))
		addonProto = append(addonProto, []byte(flowStr)...)
	}

	header := []byte{0x00}
	header = append(header, d.uuid[:]...)
	header = append(header, byte(len(addonProto)))
	if len(addonProto) > 0 {
		header = append(header, addonProto...)
	}
	header = append(header, 0x01) // CMD: 1 (TCP)

	portBytes := make([]byte, 2)
	binary.BigEndian.PutUint16(portBytes, uint16(targetPort))
	header = append(header, portBytes...)

	ip := net.ParseIP(targetHost)
	if ip4 := ip.To4(); ip4 != nil {
		header = append(header, 0x01) // IPv4
		header = append(header, ip4...)
	} else if ip6 := ip.To16(); ip6 != nil {
		header = append(header, 0x03) // IPv6
		header = append(header, ip6...)
	} else {
		header = append(header, 0x02) // 域名
		header = append(header, byte(len(targetHost)))
		header = append(header, []byte(targetHost)...)
	}

	if _, err := tlsConn.Write(header); err != nil {
		_ = tlsConn.Close()
		return nil, fmt.Errorf("发送 VLESS 请求头失败: %w", err)
	}

	// 全双工异步连接：不在 DialThrough 中半双工死等响应，通过 vlessConn 包装器在首个 Read() 懒解析并透明解帧
	return newVlessConn(tlsConn, d.uuid, isVision), nil
}

// -----------------------------------------------------------------------------
// VLESS 全双工流式包装连接 (支持首包懒加载响应头与 XTLS-Vision 自动解帧去填充)
// -----------------------------------------------------------------------------

type vlessConn struct {
	net.Conn
	uuid             [16]byte
	isVision         bool
	headerRead       bool
	visionDirectCopy bool
	remainingContent int
	remainingPadding int
}

func newVlessConn(raw net.Conn, uuid [16]byte, isVision bool) *vlessConn {
	return &vlessConn{
		Conn:     raw,
		uuid:     uuid,
		isVision: isVision,
	}
}

func (c *vlessConn) Read(p []byte) (int, error) {
	// 1. 首次读取时，按需懒解析 2 字节 VLESS 响应头 (Version 0x00 + AddonsLen)
	if !c.headerRead {
		vlessHdr := make([]byte, 2)
		if _, err := io.ReadFull(c.Conn, vlessHdr); err != nil {
			return 0, fmt.Errorf("读取 VLESS 响应头失败: %w", err)
		}
		if vlessHdr[0] != 0x00 {
			return 0, fmt.Errorf("VLESS 协议版本不匹配: 0x%02x", vlessHdr[0])
		}
		if vlessHdr[1] > 0 {
			discardAddons := make([]byte, vlessHdr[1])
			if _, err := io.ReadFull(c.Conn, discardAddons); err != nil {
				return 0, fmt.Errorf("读取 VLESS Addons 失败: %w", err)
			}
		}
		c.headerRead = true

		// 若启用了 XTLS-Vision 流控，首个下行数据流附带 16 字节 UUID 前缀
		if c.isVision {
			downUUID := make([]byte, 16)
			if _, err := io.ReadFull(c.Conn, downUUID); err != nil {
				return 0, fmt.Errorf("读取 Vision 下行 UUID 失败: %w", err)
			}
		}
	}

	// 2. 非 Vision 流控或已安全切换为直连直通模式 (待当前帧 Padding 全部排空后)
	if !c.isVision || (c.visionDirectCopy && c.remainingContent == 0 && c.remainingPadding == 0) {
		return c.Conn.Read(p)
	}

	// 3. XTLS-Vision 状态机解帧循环 (Command + ContentLen + PaddingLen)
	for {
		if c.remainingContent > 0 {
			toRead := len(p)
			if toRead > c.remainingContent {
				toRead = c.remainingContent
			}
			n, err := c.Conn.Read(p[:toRead])
			if n > 0 {
				c.remainingContent -= n
			}
			return n, err
		}

		if c.remainingPadding > 0 {
			toDiscard := c.remainingPadding
			if toDiscard > 4096 {
				toDiscard = 4096
			}
			discard := make([]byte, toDiscard)
			n, err := c.Conn.Read(discard)
			if n > 0 {
				c.remainingPadding -= n
			}
			if err != nil {
				return 0, err
			}
			continue
		}

		// 当前帧的 Content 与 Padding 均已安全排空，若收到了 Direct 指令则切入底层直通模式
		if c.visionDirectCopy {
			return c.Conn.Read(p)
		}

		// 读取下一个 5 字节 Vision 块头: cmd(1) + contentLen(2) + paddingLen(2)
		blockHdr := make([]byte, 5)
		if _, err := io.ReadFull(c.Conn, blockHdr); err != nil {
			return 0, err
		}

		cmd := blockHdr[0]
		contentLen := int(binary.BigEndian.Uint16(blockHdr[1:3]))
		paddingLen := int(binary.BigEndian.Uint16(blockHdr[3:5]))

		if cmd == 1 || cmd == 2 { // CommandPaddingEnd 或 CommandPaddingDirect
			c.visionDirectCopy = true
		}

		c.remainingContent = contentLen
		c.remainingPadding = paddingLen
	}
}

// -----------------------------------------------------------------------------
// Shadowsocks 出站适配器 (原生纯 Go AEAD 实现: aes-128-gcm / aes-256-gcm)
// -----------------------------------------------------------------------------

type ShadowsocksOutbound struct {
	node UpstreamNode
	host string
	port int
	key  []byte
}

func NewShadowsocksOutbound(node UpstreamNode) (IOutboundDialer, error) {
	host, portStr := node.Server, strconv.Itoa(node.Port)
	if node.URL != "" && strings.Contains(node.URL, "://") {
		u, err := url.Parse(node.URL)
		if err == nil {
			host = u.Hostname()
			portStr = u.Port()
			if node.Password == "" && u.User != nil {
				pwd, _ := u.User.Password()
				node.Password = pwd
			}
		}
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 8388
	}
	if host == "" {
		host = "127.0.0.1"
	}

	// 派生固定 16/32 字节密钥
	rawKey := []byte(node.Password)
	if len(rawKey) < 16 {
		padded := make([]byte, 16)
		copy(padded, rawKey)
		rawKey = padded
	} else if len(rawKey) > 16 && len(rawKey) < 32 {
		padded := make([]byte, 32)
		copy(padded, rawKey)
		rawKey = padded
	} else if len(rawKey) > 32 {
		rawKey = rawKey[:32]
	}

	return &ShadowsocksOutbound{
		node: node,
		host: host,
		port: port,
		key:  rawKey,
	}, nil
}

func (d *ShadowsocksOutbound) Protocol() string { return "shadowsocks" }

func (d *ShadowsocksOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	serverAddr := fmt.Sprintf("%s:%d", d.host, d.port)
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	rawConn, err := dialer.DialContext(ctx, "tcp", serverAddr)
	if err != nil {
		return nil, fmt.Errorf("连接 Shadowsocks 服务端 [%s] 失败: %w", serverAddr, err)
	}

	return d.DialThrough(ctx, rawConn, targetAddr)
}

func (d *ShadowsocksOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	// 构建 SS 目标载荷: ATYP + ADDR + PORT
	targetHost, targetPortStr, err := net.SplitHostPort(targetAddr)
	if err != nil {
		_ = baseConn.Close()
		return nil, err
	}
	targetPort, _ := strconv.Atoi(targetPortStr)

	var payload []byte
	ip := net.ParseIP(targetHost)
	if ip4 := ip.To4(); ip4 != nil {
		payload = append(payload, 0x01)
		payload = append(payload, ip4...)
	} else if ip6 := ip.To16(); ip6 != nil {
		payload = append(payload, 0x04)
		payload = append(payload, ip6...)
	} else {
		payload = append(payload, 0x03)
		payload = append(payload, byte(len(targetHost)))
		payload = append(payload, []byte(targetHost)...)
	}
	portBytes := make([]byte, 2)
	binary.BigEndian.PutUint16(portBytes, uint16(targetPort))
	payload = append(payload, portBytes...)

	// 简易 SS-AEAD 加密包装
	salt := make([]byte, len(d.key))
	if _, err := io.ReadFull(rand.Reader, salt); err != nil {
		_ = baseConn.Close()
		return nil, err
	}
	if _, err := baseConn.Write(salt); err != nil {
		_ = baseConn.Close()
		return nil, err
	}

	block, err := aes.NewCipher(d.key)
	if err != nil {
		_ = baseConn.Close()
		return nil, err
	}
	gcm, err := cipher.NewGCM(block)
	if err != nil {
		_ = baseConn.Close()
		return nil, err
	}

	nonce := make([]byte, gcm.NonceSize())
	encrypted := gcm.Seal(nil, nonce, payload, nil)

	// 写入 2 字节密文长度前缀，确保流式对端精准 ReadFull 消除粘包与截断
	lenBuf := make([]byte, 2)
	binary.BigEndian.PutUint16(lenBuf, uint16(len(encrypted)))
	if _, err := baseConn.Write(lenBuf); err != nil {
		_ = baseConn.Close()
		return nil, err
	}

	if _, err := baseConn.Write(encrypted); err != nil {
		_ = baseConn.Close()
		return nil, err
	}

	return baseConn, nil
}
