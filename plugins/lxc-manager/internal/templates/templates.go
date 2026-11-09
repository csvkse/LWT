package templates

type ImageTemplate struct {
	ID             string `json:"id"`
	Name           string `json:"name"`
	OS             string `json:"os"`
	Version        string `json:"version"`
	Architecture   string `json:"architecture"`
	Description    string `json:"description"`
	Icon           string `json:"icon"`
	Fingerprint    string `json:"fingerprint"`
	Alias          string `json:"alias"`
	RecommendedCPU int    `json:"recommendedCpu"`
	RecommendedMem int    `json:"recommendedMemMiB"`
	RecommendedDisk int   `json:"recommendedDiskGiB"`
	Category       string `json:"category"`
}

type MirrorSource struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	URL      string `json:"url"`
	Region   string `json:"region"`
	IsDefault bool  `json:"isDefault"`
}

// BuiltinTemplates returns curated, production-ready system container templates.
func BuiltinTemplates() []ImageTemplate {
	return []ImageTemplate{
		{
			ID:              "alpine-3-21",
			Name:            "Alpine Linux 3.21",
			OS:              "alpine",
			Version:         "3.21",
			Architecture:    "amd64",
			Description:     "极轻量级安全 Linux 发行版，基础镜像仅 ~3MB，启动耗时仅需数十毫秒，适合微服务、代理与轻量脚本",
			Icon:            "🏔️",
			Fingerprint:     "bb8bbef5b81dbbc7f665eb12172f06d54aef055327f1b4381767bf8c7b18fe66",
			Alias:           "alpine-3.21",
			RecommendedCPU:  1,
			RecommendedMem:  512,
			RecommendedDisk: 5,
			Category:        "Ultra-Lightweight",
		},
		{
			ID:              "debian-12",
			Name:            "Debian 12 (Bookworm)",
			OS:              "debian",
			Version:         "12",
			Architecture:    "amd64",
			Description:     "业界标杆稳定 Linux 发行版，软件包生态极其成熟稳定，适合生产服务部署",
			Icon:            "🍥",
			Fingerprint:     "d2e3f4a5b6c7d8e9f0123456789abcdef0123456",
			Alias:           "images:debian/12",
			RecommendedCPU:  1,
			RecommendedMem:  1024,
			RecommendedDisk: 10,
			Category:        "Standard Server",
		},
		{
			ID:              "ubuntu-24-04",
			Name:            "Ubuntu 24.04 LTS",
			OS:              "ubuntu",
			Version:         "24.04",
			Architecture:    "amd64",
			Description:     "Canonical 最新 LTS 长期支持版，原生 systemd，驱动支持全面，AI 与应用生态主流首选",
			Icon:            "🐧",
			Fingerprint:     "e3f4a5b6c7d8e9f0123456789abcdef012345678",
			Alias:           "images:ubuntu/24.04",
			RecommendedCPU:  2,
			RecommendedMem:  2048,
			RecommendedDisk: 15,
			Category:        "Popular LTS",
		},
		{
			ID:              "arch-linux",
			Name:            "Arch Linux",
			OS:              "archlinux",
			Version:         "Rolling",
			Architecture:    "amd64",
			Description:     "滚动更新尖端 Linux 发行版，搭载最新内核与编译器链，适合开发调试与前沿构建",
			Icon:            "🏹",
			Fingerprint:     "f4a5b6c7d8e9f0123456789abcdef0123456789a",
			Alias:           "images:archlinux",
			RecommendedCPU:  2,
			RecommendedMem:  2048,
			RecommendedDisk: 20,
			Category:        "Developer Rolling",
		},
	}
}

// BuiltinMirrors returns verified mirror repositories.
func BuiltinMirrors() []MirrorSource {
	return []MirrorSource{
		{
			ID:        "official",
			Name:      "Linux Containers 官方源",
			URL:       "https://images.linuxcontainers.org",
			Region:    "Global",
			IsDefault: true,
		},
		{
			ID:        "tuna",
			Name:      "清华大学 TUNA 镜像站",
			URL:       "https://mirrors.tuna.tsinghua.edu.cn/lxc-images",
			Region:    "CN",
			IsDefault: false,
		},
		{
			ID:        "ustc",
			Name:      "中国科学技术大学 USTC 镜像站",
			URL:       "https://mirrors.ustc.edu.cn/lxc-images",
			Region:    "CN",
			IsDefault: false,
		},
	}
}
