package main

import (
	"net"
	"testing"
	"time"
)

func BenchmarkDnsLruCacheHotGetParallel(b *testing.B) {
	cache := NewDnsLruCache(1024, time.Minute, 5*time.Minute, time.Second)
	cache.Set("hot.example", []net.IP{net.ParseIP("192.0.2.10")})
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			if _, _, ok := cache.Get("hot.example"); !ok {
				b.Fatal("hot cache entry was not found")
			}
		}
	})
}

func BenchmarkDnsLruCacheManyKeyGetParallel(b *testing.B) {
	const keyCount = 64
	cache := NewDnsLruCache(1024, time.Minute, 5*time.Minute, time.Second)
	keys := make([]string, keyCount)
	for i := range keys {
		keys[i] = "host-" + string(rune('a'+i%26)) + "-" + string(rune('A'+i/26)) + ".example"
		cache.Set(keys[i], []net.IP{net.ParseIP("192.0.2.10")})
	}
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		index := 0
		for pb.Next() {
			if _, _, ok := cache.Get(keys[index%len(keys)]); !ok {
				b.Fatal("cache entry was not found")
			}
			index++
		}
	})
}
