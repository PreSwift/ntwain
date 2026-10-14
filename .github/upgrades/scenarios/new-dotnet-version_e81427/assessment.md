# Projects and dependencies analysis

This document provides a comprehensive overview of the projects and their dependencies in the context of upgrading to .NETCoreApp,Version=v10.0.

## Table of Contents

- [Executive Summary](#executive-Summary)
  - [Highlevel Metrics](#highlevel-metrics)
  - [Projects Compatibility](#projects-compatibility)
  - [Package Compatibility](#package-compatibility)
  - [API Compatibility](#api-compatibility)
- [Aggregate NuGet packages details](#aggregate-nuget-packages-details)
- [Top API Migration Challenges](#top-api-migration-challenges)
  - [Technologies and Features](#technologies-and-features)
  - [Most Frequent API Issues](#most-frequent-api-issues)
- [Projects Relationship Graph](#projects-relationship-graph)
- [Project Details](#project-details)

  - [samples\Sample.WPF\Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj)


## Executive Summary

### Highlevel Metrics

| Metric | Count | Status |
| :--- | :---: | :--- |
| Total Projects | 1 | All require upgrade |
| Total NuGet Packages | 7 | 5 need upgrade |
| Total Code Files | 10 |  |
| Total Code Files with Incidents | 1 |  |
| Total Lines of Code | 1422 |  |
| Total Number of Issues | 8 |  |
| Estimated LOC to modify | 0+ | at least 0.0% of codebase |

### Projects Compatibility

| Project | Target Framework | Difficulty | Package Issues | API Issues | Est. LOC Impact | Description |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| [samples\Sample.WPF\Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | net48 | 🟢 Low | 6 | 0 |  | ClassicWinForms, Sdk Style = False |

### Package Compatibility

| Status | Count | Percentage |
| :--- | :---: | :---: |
| ✅ Compatible | 2 | 28.6% |
| ⚠️ Incompatible | 5 | 71.4% |
| 🔄 Upgrade Recommended | 0 | 0.0% |
| ***Total NuGet Packages*** | ***7*** | ***100%*** |

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

## Aggregate NuGet packages details

| Package | Current Version | Suggested Version | Projects | Description |
| :--- | :---: | :---: | :--- | :--- |
| CommonServiceLocator | 1.3 | 2.0.7 | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ⚠️NuGet 包不兼容 |
| Microsoft.WindowsAPICodePack-Core | 1.1.0.2 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ✅Compatible |
| Microsoft.WindowsAPICodePack-Shell | 1.1.0.0 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ✅Compatible |
| ModernWpf | 2.0.0-alpha94 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ⚠️NuGet 包不兼容 |
| ModernWpf.Core | 2.0.0-alpha96 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ⚠️NuGet 包不兼容 |
| MvvmLightLibs | 5.3.0.0 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ⚠️NuGet 包不兼容 |
| NTwain | 3.7.5 |  | [Sample.WPF.csproj](#samplessamplewpfsamplewpfcsproj) | ⚠️NuGet 包不兼容 |

## Top API Migration Challenges

### Technologies and Features

| Technology | Issues | Percentage | Migration Path |
| :--- | :---: | :---: | :--- |

### Most Frequent API Issues

| API | Count | Percentage | Category |
| :--- | :---: | :---: | :--- |

## Projects Relationship Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart LR
    P1["<b>⚙️&nbsp;Sample.WPF.csproj</b><br/><small>net48</small>"]
    click P1 "#samplessamplewpfsamplewpfcsproj"

```

## Project Details

<a id="samplessamplewpfsamplewpfcsproj"></a>
### samples\Sample.WPF\Sample.WPF.csproj

#### Project Info

- **Current Target Framework:** net48
- **Proposed Target Framework:** net10.0-windows
- **SDK-style**: False
- **Project Kind:** ClassicWinForms
- **Dependencies**: 0
- **Dependants**: 0
- **Number of Files**: 11
- **Number of Files with Incidents**: 1
- **Lines of Code**: 1422
- **Estimated LOC to modify**: 0+ (at least 0.0% of the project)

#### Dependency Graph

Legend:
📦 SDK-style project
⚙️ Classic project

```mermaid
flowchart TB
    subgraph current["Sample.WPF.csproj"]
        MAIN["<b>⚙️&nbsp;Sample.WPF.csproj</b><br/><small>net48</small>"]
        click MAIN "#samplessamplewpfsamplewpfcsproj"
    end

```

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 0 | High - Require code changes |
| 🟡 Source Incompatible | 0 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 0 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 0 |  |
| ***Total APIs Analyzed*** | ***0*** |  |

