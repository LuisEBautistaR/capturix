<p align="center"><img src="assets/logo/capturix-logo-128.png" width="96" alt="Logo de Capturix"></p>

# Capturix

Herramienta de captura de pantalla para Windows escrita desde cero en C# / .NET 8: capturas,
anotaciones, grabación de vídeo y GIF, y lectura de texto con OCR. Un proyecto del laboratorio REA.

**Descarga:** <https://protohub.digital/capturix/> o la sección *Releases* de este repositorio.
Software libre con licencia [MIT](LICENSE). Política de firma de código:
[Code signing policy](CODE_SIGNING_POLICY.md).

## Estado

| Fase | Contenido | Estado |
|---|---|---|
| 1 | App de bandeja, atajos globales, captura de región / ventana / pantalla completa, selector con lupa | ✅ |
| 2 | Editor con anotaciones (flecha, texto, formas, pixelado, recorte, pasos), deshacer/rehacer | ✅ |
| 3 | Formato propio `.cpx`, biblioteca con miniaturas y autoguardado, un solo editor por named pipe | ✅ |
| 4 | Captura con desplazamiento (scroll) vertical con unión automática | ✅ |
| 5 | Grabación de pantalla a MP4 (H.264 + AAC con sonido del sistema y micrófono) y GIF animado | ✅ |
| 6 | OCR: texto de una imagen, de una zona del editor o directo de la pantalla al portapapeles | ✅ |
| 7 | Modo oscuro persistente, guardado local / OneDrive / Google Drive, logo e icono propios | ✅ |
| 1.1 | Sellos con emoji a color en el editor | ✅ |

## Uso

```
dotnet build
artifacts\bin\Debug\Capturix.exe
```

Aparece un icono en la bandeja del sistema. Atajos por defecto:

| Atajo | Acción |
|---|---|
| `Ctrl+Shift+A` | Selector de región (clic = ventana bajo el cursor, arrastrar = región, flechas = mover 1 px, Esc = cancelar) |
| `Ctrl+Shift+F` | Pantalla completa (todos los monitores; `"FullScreenMode": "cursor"` para solo el monitor del ratón) |
| `Ctrl+Shift+W` | Ventana activa |
| `Ctrl+Shift+D` | Captura con desplazamiento: elige ventana o región, se desplaza sola hasta el final (Esc para terminar antes) |
| `Ctrl+Shift+T` | Capturar texto: elige una zona y su texto (OCR) se copia al portapapeles; no se guarda imagen |
| `Ctrl+Shift+R` | Grabar vídeo MP4: elige ventana o región, cuenta atrás de 3 s; el mismo atajo (o «Detener») termina |
| `Ctrl+Shift+G` | Grabar GIF animado (igual que el vídeo) |

Las capturas se guardan como documentos editables `.cpx` en la biblioteca (ver *Dónde se guardan*),
se copian al portapapeles y se abren en el editor (`"OpenEditorAfterCapture": false` para
desactivarlo; `"SavePngCopy": true` guarda también un `.png`).

### Dónde se guardan las capturas

Menú de la bandeja → **Guardar capturas en**:

| Opción | Carpeta |
|---|---|
| Local | `%USERPROFILE%\Capturix` — solo en este equipo, nunca se sincroniza |
| OneDrive | `<carpeta de OneDrive>\Capturix` |
| Google Drive | `<unidad de Google Drive>\Mi unidad\Capturix` (requiere Google Drive para escritorio) |
| Otra carpeta… | la que elijas |

Capturix no inicia sesión en ningún servicio ni guarda contraseñas o tokens: escribe en la carpeta
que sincroniza la app oficial de OneDrive o de Google Drive, y es esa app la que sube los archivos
(y la que permite trabajar sin conexión). Las carpetas se detectan solas (variables de entorno y
registro de OneDrive; registro de DriveFS y unidades con «Mi unidad»/«My Drive» de Google Drive).
Si OneDrive está instalado pero sin sesión iniciada, el menú y el editor lo avisan (⚠), porque en
ese caso no se sube nada. Al cambiar de ubicación, Capturix ofrece mover las capturas existentes
(los nombres repetidos se renombran, nunca se sobrescriben). Si la app de la nube desaparece, las
capturas van a la carpeta local en vez de perderse.

Los ajustes de antes de esta opción siguen usando su carpeta (`Documentos\Capturix`) hasta que
elijas otra.

### Tema claro / oscuro

Menú de la bandeja → **Tema** (Sistema, Claro u Oscuro) o el botón «Tema» del editor. La elección
se guarda en `settings.json` (`"Theme"`), la comparten la bandeja y el editor (el editor cambia en
vivo si se cambia desde la bandeja) y «Sistema» sigue la configuración de colores de Windows,
también en vivo. Incluye barra de título oscura (DWM) y menú de la bandeja oscuro.

Solo hay un editor abierto: las capturas nuevas (o un segundo `Capturix.Editor.exe archivo`) se le
pasan por un named pipe propio del usuario. Los documentos de la biblioteca se autoguardan 1,2 s
después de cada cambio; la bandeja inferior muestra las capturas recientes con miniatura.

### Captura con desplazamiento

Mueve la rueda del ratón sobre el área elegida y une los fotogramas: cada fila de píxeles se
reduce a un hash; las filas iguales en la misma posición arriba y abajo son bandas fijas
(cabeceras, barras de estado) y salen una sola vez; las columnas que no cambian (bordes de
ventana translúcidos, barras laterales) y la barra de desplazamiento se excluyen al buscar el
desplazamiento exacto entre fotogramas. Ajusta el salto para solaparse ~45 %, retrocede si
pierde el solape y termina tras 3 pasos sin movimiento. Solo vertical; no puede desplazar
ventanas de administrador (haría falta un proceso aparte con `uiAccess`).

Autoprueba: `Capturix.exe --selftest-scroll carpeta` (página sintética, comparación píxel a píxel).
Depuración: con `CAPTURIX_DEBUG_SCROLL=carpeta` guarda cada fotograma y registra cada unión en el log.

### Grabación de pantalla

Un hilo captura a ritmo fijo (`BitBlt` a una sección DIB, con el cursor dibujado encima) y otro
codifica; si el codificador se retrasa se descartan fotogramas en vez de desincronizar. MP4: H.264
mediante el Sink Writer de **Media Foundation** (incluido en Windows, por interop COM, sin
dependencias; usa el codificador de la GPU si existe). GIF: codificador propio con paleta por
fotograma, solo el rectángulo que cambia, píxeles sin cambios transparentes y fotogramas
idénticos fusionados. El marco rojo y la barra de control no salen en el vídeo
(`WDA_EXCLUDEFROMCAPTURE`). Ajustes: `VideoFps` (30), `GifFps` (12), `RecordCursor`.

**Audio (solo MP4):** sonido del sistema por WASAPI *loopback* y micrófono por WASAPI, ambos
convertidos a 48 kHz estéreo y mezclados sobre el mismo reloj que el vídeo (con 150 ms de margen
para la latencia; los silencios del loopback, que no entrega nada mientras no suena nada, se
rellenan con ceros). Pista AAC de 192 kbit/s. Se activan en el menú de la bandeja: «Grabar sonido
del sistema» (activado por defecto) y «Grabar micrófono» (desactivado por defecto).

Autopruebas: `Capturix.exe --selftest-record carpeta` (incluye MP4 con tono de 440 Hz) y
`Capturix.exe --selftest-audio-devices carpeta` (abre altavoces y micrófono 1,5 s y solo informa
del formato y el nivel; no guarda audio).

### Sellos con emoji (1.1)

Herramienta «Sello» del editor (tecla `M`): un clic pone el emoji elegido y sus tiradores cambian el
tamaño (con `Mayús`, cuadrado). El botón «Sello ▾» abre 30 emojis: caras (😀 😂 😍 😎 🤔 😮 😢 😭 😡 😴),
manos (👍 👎 👏 👉 👀 🙏), marcas (✅ ❌ ⚠️ ❓ ❗ 💯) y cosas (❤️ ⭐ 🔥 💡 🐞 🔒 📌 🎉); elegir uno con un
sello seleccionado lo cambia.

WPF dibuja los emojis en blanco y negro, así que `EmojiRenderer` lee las capas de color de la propia
fuente de Windows (Segoe UI Emoji): la tabla COLR dice qué glifos forman cada emoji y la tabla CPAL de
qué color se rellena cada capa. Cada capa es una forma vectorial: nítido a cualquier tamaño, igual que
en Windows y sin imágenes incluidas. Los documentos con sellos se guardan como formato `.cpx` 2; los
que no tienen sellos siguen siendo formato 1 y los abre Capturix 1.0.

### OCR (reconocimiento de texto)

Usa el motor OCR que trae Windows (`Windows.Media.Ocr`: sin conexión, sin coste y sin
dependencias). Reconoce los idiomas de OCR
instalados en Windows (Configuración → Hora e idioma → Idioma; se usan los idiomas preferidos
del usuario). Las imágenes pequeñas se amplían ×2 antes de reconocer, porque el texto de
pantalla es diminuto para el motor.

- **Bandeja** (`Ctrl+Shift+T` o «Capturar texto (OCR)»): eliges una zona y el texto va al
  portapapeles, con una vista previa en la notificación.
- **Editor**: «Extraer texto» (`Ctrl+T`) lee toda la imagen; la herramienta «Texto OCR» (`O`)
  lee solo la zona que arrastres. El resultado se puede corregir y copiar (todo o la selección).
  Se reconoce la imagen *con* anotaciones, así que el texto tapado con «Pixelar» tampoco lo lee
  el OCR.

Autoprueba: `Capturix.exe --selftest-ocr carpeta` (texto sintético de interfaz, tema oscuro y
código; comprueba palabras, líneas y cajas, y que una imagen sin texto no invente nada).

### Formato `.cpx`

ZIP con: `index.json` (versión y páginas), `metadata.json` (fecha, origen, etiquetas),
`thumbnail.png` (vista previa), `<id>.json` (anotaciones con coordenadas en píxeles y colores
`#AARRGGBB`) y `<id>.png` (imagen original sin tocar). Se escribe en un archivo temporal y se
reemplaza de golpe, para que un autoguardado interrumpido no corrompa el documento.

### Editor (`Capturix.Editor.exe [imagen]`)

| Tecla | Herramienta | | Tecla | Acción |
|---|---|---|---|---|
| `V` | Seleccionar / mover / tiradores | | `Ctrl+Z` / `Ctrl+Y` | Deshacer / rehacer |
| `A` | Flecha | | `Ctrl+C` | Copiar con anotaciones |
| `L` | Línea | | `Ctrl+S` | Guardar como PNG/JPEG |
| `R` / `E` | Rectángulo / elipse | | `Ctrl+O` / `Ctrl+V` | Abrir / pegar imagen |
| `T` | Texto (Enter confirma) | | `Supr` | Borrar selección |
| `H` | Resaltador | | Flechas | Mover selección (Mayús: 10 px) |
| `B` | Pixelar (ocultar datos) | | `Mayús` + arrastrar | 45° / cuadrado / círculo |
| `S` | Pasos numerados | | `Ctrl+rueda`, `Ctrl+0`, `Ctrl+1` | Zoom, ajustar, 100 % |
| `P` | Lápiz | | | |
| `C` | Recortar | | `Ctrl+T` | Extraer texto (OCR) de toda la imagen |
| `O` | Texto OCR de una zona | | | |
| `M` | Sello con emoji | | | |

Autoprueba sin interfaz: `Capturix.Editor.exe --selftest entrada.png salida.png` (informe en
`salida.png.txt`; incluye ida y vuelta `.cpx` y OCR con pixelado).
Autoprueba de almacenamiento: `Capturix.exe --selftest-storage carpeta` (detección de OneDrive /
Google Drive y movimiento de capturas, solo con archivos de prueba dentro de `carpeta`).
Ajustes: `%APPDATA%\Capturix\settings.json` (menú de la bandeja → *Editar ajustes*).
Log: `%APPDATA%\Capturix\capturix.log`.

## Estructura

- `src/Capturix.Common` — sin interfaz: ajustes, tema, ubicaciones de guardado (local / nube), log, formato `.cpx` y pipe del editor.
- `src/Capturix.Core` — captura (GDI `BitBlt` con `CAPTUREBLT`), enumeración de ventanas, almacenamiento `.cpx`, atajos.
- `src/Capturix.Capture` — app de bandeja (`Capturix.exe`) y selector de región.
- `src/Capturix.Ocr` — envoltorio del OCR de Windows (único proyecto con TFM `net8.0-windows10.0.19041.0`, que trae las proyecciones WinRT).
- `src/Capturix.Editor` — editor WPF (`Capturix.Editor.exe`): modelo de anotaciones, deshacer/rehacer por instantáneas, exportación.
- `assets/logo` — logo (SVG, PNG de 64 a 1024 px e icono `.ico` multitamaño). Se genera con
  `dotnet run --project tools/LogoGen -- assets/logo`: el dibujo está definido una sola vez en
  `tools/LogoGen/Program.cs` (marco de captura + «C» + punto de grabación; a 16–24 px usa una
  versión simplificada para que se lea).

## Compilar y publicar

Requisitos: Windows 10/11, .NET 8 SDK e Inno Setup 6.

```powershell
dotnet build                                                   # Debug en artifacts\bin\Debug
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1   # instalador en artifacts\installer
```

Las versiones publicadas las compila GitHub Actions (`.github/workflows/release.yml`) al subir una
etiqueta `vX.Y.Z`: publica el código autocontenido, crea el instalador, lo envía a SignPath para
firmarlo (con aprobación manual) y lo adjunta a una Release junto con su huella SHA-256. La
configuración que espera SignPath está en `.signpath/artifact-configuration.xml`.

## Licencia

[MIT](LICENSE) © 2026 Luis E. Bautista.

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).
