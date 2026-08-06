<p align="center">
  <img src="assets/app-icon.png" width="128" alt="无损拼图图标">
</p>

<h1 align="center">无损拼图</h1>

<p align="center">面向大尺寸图片的 Windows 单文件拼接与海报合集工具</p>

## 下载

前往 [Releases](https://github.com/wjx-wp/lossless-image-stitcher/releases/latest) 下载最新版 `无损拼图.exe`。程序为单个 EXE，不需要安装，也不会上传图片；所有处理均在本机完成。

运行环境：Windows 10/11，系统需具备 .NET Framework 4 运行环境。

## 主要功能

- 原始像素拼接：竖向或横向拼接，不缩放、不重采样，导出无损 PNG。
- 日常排序：拖入多张图片、勾选参与项、拖动调整顺序。
- 灵活画布：支持对齐方式、间距、边距、透明/白色/自定义背景。
- 大图友好：按行流式写入 PNG，降低超长图导出时的内存压力。
- 带名称合集：从 `01_Mexico.png` 等文件名自动提取 `Mexico`，也可逐项修改。
- 自动布局：根据图片数量与比例选择协调的行列数，最后一行自动居中。
- 分享版输出：合集可导出高质量 JPEG（默认质量 92）或 PNG，在清晰度与体积之间自由选择。
- 多尺寸图标：窗口、任务栏、Alt+Tab 和 EXE 文件均使用同一套内嵌图标。

更完整的操作说明见 [README_使用说明.md](README_%E4%BD%BF%E7%94%A8%E8%AF%B4%E6%98%8E.md)。

## “无损”的含义

无损模式会保留图片解码后的原始像素，不会为了统一宽高而拉伸图片，并将拼接结果写入 PNG。JPEG 原图在之前保存时已经产生的压缩损失无法恢复，但本工具不会在拼接过程中再次加入 JPEG 压缩。

“带名称合集”是面向分享和预览的另一种模式，会按设置高质量缩小海报；它不属于原始像素无损拼接。

## 从源码构建

在 Windows PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

构建脚本使用系统自带的 .NET Framework C# 编译器，输出文件为 `dist\无损拼图.exe`。图标会直接嵌入 EXE，不需要随程序分发 `assets` 目录。

## 项目结构

```text
assets/                  应用图标源文件与多尺寸 ICO
src/LosslessStitcher/    WinForms 源码
build.ps1                单文件构建脚本
README_使用说明.md        中文使用说明
```

## 许可

当前仓库尚未附加开源许可证；公开可见不等同于授予复制、修改或再分发许可。
