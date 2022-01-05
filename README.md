# Torrent Service

*Copyright © 2010–2022 Dontnod Entertainment SA*

This .NET service monitors directories and automatically downloads torrents using
the Bittorrent protocol.

### Usage

```sh
TorrentService configurationfile.json
```

### Building for Linux

```sh
dotnet build -c:Release TorrentService/TorrentService.csproj --runtime linux-x64
```

