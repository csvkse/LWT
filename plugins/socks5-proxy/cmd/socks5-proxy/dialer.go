package main

import (
	"context"
	"errors"
	"fmt"
	"net"
	"strconv"
	"sync"
	"time"
)

type dialResult struct {
	conn net.Conn
	ip   net.IP
	err  error
}

// ErrAclForbidden 表示候选目标 IP 均被 ACL 子网黑白名单拒绝
var ErrAclForbidden = errors.New("all resolved IPs rejected by subnet ACL policy")

// dialUpstreamWithRace 实现了 RFC 8305 阶梯并发拨号竞速算法 (Happy Eyeballs Staggered Race)
// 针对 CDN 或多 A 记录域名，并发竞速拨号，优先选用最快建立 TCP 握手的 IP，消除首个 IP 慢节点阻塞造成的长尾卡顿。
func dialUpstreamWithRace(
	ctx context.Context,
	ips []net.IP,
	port int,
	timeout time.Duration,
	acl *AclEngine,
) (net.Conn, net.IP, error) {
	if len(ips) == 0 {
		return nil, nil, errors.New("no upstream IP available to dial")
	}

	// 1. 过滤通过 ACL Subnet 策略的合法候选 IP (Anti-DNS-Rebinding 预检)
	var candidates []net.IP
	for _, ip := range ips {
		if acl == nil || acl.IsIpAllowed(ip) {
			candidates = append(candidates, ip)
		}
	}

	if len(candidates) == 0 {
		return nil, nil, ErrAclForbidden
	}

	// 2. 单 IP 场景直接快速拨号，零竞速开销
	if len(candidates) == 1 {
		targetAddr := net.JoinHostPort(candidates[0].String(), strconv.Itoa(port))
		dialer := net.Dialer{Timeout: timeout}
		conn, err := dialer.DialContext(ctx, "tcp", targetAddr)
		if err != nil {
			return nil, nil, err
		}
		optimizeTcpConn(conn)
		return conn, candidates[0], nil
	}

	// 3. 多 IP 场景执行 Happy Eyeballs 阶梯竞速 (最多取前 4 个 IP)
	maxCandidates := candidates
	if len(maxCandidates) > 4 {
		maxCandidates = maxCandidates[:4]
	}

	raceCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	resultCh := make(chan dialResult, len(maxCandidates))
	var winOnce sync.Once
	var winningConn net.Conn
	var winningIP net.IP

	// 阶梯延迟：120ms 首个未连通则并发拉起下一个候选 IP
	const staggerDelay = 120 * time.Millisecond
	staggerTimer := time.NewTimer(0) // 立即启动第一个
	defer staggerTimer.Stop()

	dialCandidate := func(ip net.IP) {
		targetAddr := net.JoinHostPort(ip.String(), strconv.Itoa(port))
		dialer := net.Dialer{Timeout: timeout}
		conn, err := dialer.DialContext(raceCtx, "tcp", targetAddr)
		if err != nil {
			resultCh <- dialResult{ip: ip, err: err}
			return
		}
		optimizeTcpConn(conn)

		// 竞速检查：首个完成建连的连接胜出
		won := false
		winOnce.Do(func() {
			won = true
			winningConn = conn
			winningIP = ip
			cancel() // 及时通知其他在途拨号取消
		})

		if won {
			resultCh <- dialResult{conn: conn, ip: ip}
		} else {
			// 已有更快的连接胜出，立即关闭较慢的连接
			_ = conn.Close()
		}
	}

	started := 0
	finished := 0
	var lastErr error

	for finished < len(maxCandidates) {
		if started < len(maxCandidates) {
			select {
			case <-staggerTimer.C:
				go dialCandidate(maxCandidates[started])
				started++
				if started < len(maxCandidates) {
					staggerTimer.Reset(staggerDelay)
				}
			case res := <-resultCh:
				finished++
				if res.conn != nil {
					return res.conn, res.ip, nil
				}
				lastErr = res.err
				// 首个节点若立即报错，立即唤醒下一个候选节点，无需干等 120ms
				if started < len(maxCandidates) {
					staggerTimer.Reset(0)
				}
			case <-ctx.Done():
				return nil, nil, ctx.Err()
			}
		} else {
			// 所有候选协程已拉起，等待首个建连成功或全部失败
			select {
			case res := <-resultCh:
				finished++
				if res.conn != nil {
					return res.conn, res.ip, nil
				}
				lastErr = res.err
			case <-ctx.Done():
				return nil, nil, ctx.Err()
			}
		}
	}

	if winningConn != nil {
		return winningConn, winningIP, nil
	}

	if lastErr == nil {
		lastErr = fmt.Errorf("all %d candidate IPs failed to connect", len(maxCandidates))
	}
	return nil, nil, lastErr
}
