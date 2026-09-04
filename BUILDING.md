# Сборка SpeechRibbon

## Требования

- Windows 10/11 x64;
- Git и Git LFS;
- .NET SDK `8.0.301` и MSBuild `17.10.4`;
- w64devkit `2.9.1`, распакованный в `tools\w64devkit-2.9.1\w64devkit\bin`.

w64devkit не хранится в репозитории. Получите архив версии `2.9.1` из официального проекта w64devkit и распакуйте его в указанный путь. Содержимое `third_party\bundled` уже связано размерами и SHA-256 с `third_party\BUNDLED-ASSETS.json`.

## Получение крупных файлов

```powershell
git lfs install
git lfs pull
```

Проверьте, что Git LFS не оставил pointer-файлы:

```powershell
git lfs ls-files
Get-Item .\third_party\bundled\ggml-small-q8_0.bin
Get-Item .\dist\SpeechRibbon-0.0.6.exe
```

Ожидаемые размеры и хэши сторонних build-входов находятся в `third_party\BUNDLED-ASSETS.json`.

## Сборка

Из корня репозитория:

```powershell
dotnet build .\src\SpeechRibbon\SpeechRibbon.csproj -t:Rebuild -c Release -p:BundleThirdParty=false
dotnet publish .\src\SpeechRibbon\SpeechRibbon.csproj --no-build -c Release -r win-x64 --self-contained true -p:BundleThirdParty=false -o .\build\payload-0.0.6-external
New-Item -ItemType Directory -Path .\artifacts -Force | Out-Null
.\tools\launcher\Build-SpeechRibbonLauncher.ps1 -PayloadPath .\build\payload-0.0.6-external\SpeechRibbon.exe -OutputPath .\artifacts\SpeechRibbon-0.0.6.exe
```

Сборщик launcher до записи результата проверяет размер и SHA-256 каждого файла из `third_party\BUNDLED-ASSETS.json`. Версия берётся только из `Directory.Build.props`.

## Проверка результата

```powershell
$file = Get-Item .\artifacts\SpeechRibbon-0.0.6.exe
$hash = Get-FileHash $file.FullName -Algorithm SHA256
$file.VersionInfo | Select-Object FileVersion,ProductVersion
$file | Select-Object FullName,Length
$hash
```

Собранный локально EXE должен иметь версию `0.0.6.0` и запускаться на Windows x64. Он может отличаться по контрольной сумме из-за версий инструментов и метаданных сборки. Указанный в README SHA-256 относится к файлу `dist\SpeechRibbon-0.0.6.exe`.
