# Сторонние компоненты

Лицензия MIT в корне относится только к собственному коду SpeechRibbon. Встроенные зависимости и модели сохраняют собственные условия.

| Компонент | Версия/вариант | Лицензия | Источник |
|---|---|---|---|
| whisper.cpp | 1.9.2 | MIT | <https://github.com/ggml-org/whisper.cpp/tree/v1.9.2> |
| OpenAI Whisper | multilingual `small-q8_0` | MIT | <https://huggingface.co/ggerganov/whisper.cpp> |
| Silero VAD | 6.2.0 | MIT | <https://huggingface.co/ggml-org/whisper-vad/tree/main> |
| FFmpeg | 9.0.1 | LGPL 2.1 or later | <https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz> |
| MinGW-w64 runtime | bundled static portions | применимые notices | `third_party/licenses/MinGW-w64-runtime.txt` |
| Bergamot Translator | 0.4.5 | MPL 2.0 | <https://github.com/browsermt/bergamot-translator/tree/v0.4.5> |
| Firefox Translations models | tiny en→ru, base-memory ja→en | MPL 2.0 | <https://github.com/mozilla/firefox-translations-models> |
| Marian NMT | bundled with Bergamot | MIT | source tree inside `third-party-sources.zip` |
| SentencePiece | bundled with Bergamot | Apache 2.0 | source tree inside `third-party-sources.zip` |
| ssplit-cpp | bundled with Bergamot | Apache 2.0 | source tree inside `third-party-sources.zip` |
| PCRE2 | 10.39 | BSD-style | source tree inside `third-party-sources.zip` |
| .NET Runtime / Windows Desktop Runtime | self-contained runtime used by net8.0 | MIT and third-party notices | `third_party/licenses/dotnet-LICENSE.txt`, `third_party/licenses/dotnet-ThirdPartyNotices.txt` |

Полный машинно-читаемый состав встроенных файлов, их размеры и SHA-256: `third_party/BUNDLED-ASSETS.json`.

Точные исходные архивы whisper.cpp, FFmpeg и Bergamot, параметры сборки FFmpeg, лицензии и сведения о моделях находятся в `third_party/bundled/third-party-sources.zip`. Команда `SpeechRibbon-0.0.6.exe --extract-third-party <папка>` извлекает этот архив и текст notices из самого EXE.

FFmpeg используется отдельным дочерним процессом. Поставляемая сборка отключает network, GPL, nonfree и универсальные кодировщики; включён только PCM `s16le` для передачи декодированного WAV распознавателю. Изменений исходников FFmpeg нет.

Дополнительный точный текст notices, встроенный в BUILD, сохранён в `third_party/THIRD-PARTY-NOTICES.txt`. При расхождении краткой таблицы с полным текстом лицензии действует полный текст соответствующей лицензии.
