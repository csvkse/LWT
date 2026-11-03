package main

import (
	"io"
	"net"
	"sync"
	"sync/atomic"
)

var bufferPool = sync.Pool{
	New: func() any {
		b := make([]byte, 32*1024) // 32KB 缓冲区
		return &b
	},
}

// BridgeDuplexStream 在本地客户端套接字与远端上游连接之间建立双向全双工转发管道
// 并实时累加 Tx/Rx 流量计数
func BridgeDuplexStream(localConn, upstreamConn net.Conn, totalTx, totalRx *int64) {
	var wg sync.WaitGroup
	wg.Add(2)

	// 泵 1: 客户端入站 -> 远端上游 (Tx 流量)
	go func() {
		defer wg.Done()
		bufPtr := bufferPool.Get().(*[]byte)
		defer bufferPool.Put(bufPtr)

		n, _ := CopyWithBuffer(upstreamConn, localConn, *bufPtr)
		if totalTx != nil && n > 0 {
			atomic.AddInt64(totalTx, n)
		}
		_ = upstreamConn.Close()
		_ = localConn.Close()
	}()

	// 泵 2: 远端上游 -> 客户端入站 (Rx 流量)
	go func() {
		defer wg.Done()
		bufPtr := bufferPool.Get().(*[]byte)
		defer bufferPool.Put(bufPtr)

		n, _ := CopyWithBuffer(localConn, upstreamConn, *bufPtr)
		if totalRx != nil && n > 0 {
			atomic.AddInt64(totalRx, n)
		}
		_ = localConn.Close()
		_ = upstreamConn.Close()
	}()

	wg.Wait()
}

// CopyWithBuffer 使用指定缓冲区高效对拷流数据
func CopyWithBuffer(dst io.Writer, src io.Reader, buf []byte) (int64, error) {
	var written int64
	for {
		nr, er := src.Read(buf)
		if nr > 0 {
			nw, ew := dst.Write(buf[0:nr])
			if nw < 0 || nr < nw {
				nw = 0
				if ew == nil {
					ew = io.ErrShortWrite
				}
			}
			written += int64(nw)
			if ew != nil {
				return written, ew
			}
			if nr != nw {
				return written, io.ErrShortWrite
			}
		}
		if er != nil {
			if er != io.EOF {
				return written, er
			}
			break
		}
	}
	return written, nil
}
