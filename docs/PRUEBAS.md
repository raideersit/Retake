# Plan de pruebas y medición (fase 4)

Hay dos niveles: una **prueba automática** que ejecuta el motor real sin interfaz y valida los MP4, y
**pruebas manuales** con la aplicación y un juego. Al final, cómo medir CPU, GPU y RAM.

## 1. Prueba automática (`ReplayClip.SmokeTest`)

Ejecuta el mismo motor que la aplicación: captura, codifica, guarda un clip, graba unos segundos y
analiza cada MP4 con el Source Reader de Media Foundation (sin decodificar): duración, número de
muestras, intervalo entre fotogramas clave, tipos de slice H.264 y sincronía de cada pista de audio.

```powershell
cd ReplayClip
dotnet build tools\ReplayClip.SmokeTest\ReplayClip.SmokeTest.csproj -c Release
$exe = "tools\ReplayClip.SmokeTest\bin\Release\net8.0-windows10.0.22621.0\win-x64\ReplayClip.SmokeTest.exe"

# Repetición de 30 s + grabación de 10 s, con micrófono, capturando el monitor principal
& $exe --out "$env:TEMP\rc-prueba" --replay 30 --record 10 --mic

# Captura de la ventana de un juego por nombre de proceso (sin .exe)
& $exe --out "$env:TEMP\rc-prueba" --replay 15 --mode Process --process "League of Legends"

# Resistencia: una hora, guardando un clip cada 5 minutos (verifica que la memoria no crece)
& $exe --out "$env:TEMP\rc-prueba" --replay 30 --soak-minutes 60

# Analizar un MP4 cualquiera y extraer el fotograma del segundo 5 a PNG
& $exe --inspect "ruta\al\clip.mp4" --png "$env:TEMP\fotograma.png"
```

Otras opciones: `--fps 30|60`, `--bitrate 8..25`, `--wgc` (forzar Windows.Graphics.Capture en modo
monitor), `--nocursor`.

Cada archivo se marca **OK** si:

| Comprobación | Criterio |
|---|---|
| Duración del clip | N s ± 1 s (empieza en el primer fotograma clave dentro de la ventana) |
| Fotogramas clave | ninguno separado más de 1,25 s del anterior |
| Sincronía | cada pista de audio empieza a ≤100 ms y termina a ≤150 ms del video |
| Pistas | ninguna vacía |

La salida también muestra, cada 5 s: CPU, memoria, segundos y MB en el búfer, y contadores de la cadena
de video (`capturados`, `aceptados`, `repetidos`, `enviados`, `codificados`, `descartados`). Si
`descartados` crece mucho frente a `enviados`, el codificador no da abasto.

### Resultados de referencia (Windows 10 22H2, notebook con GTX 1050 y CPU de 8 hilos, monitor externo 1080p, jugando)

| Prueba | Resultado |
|---|---|
| Codificador | NVIDIA H.264 Encoder MFT, entrada en GPU sin copias |
| Clip de 30 s | 29,95 s, ~59 fps, clave cada ≤1,05 s, slices I=30 P=1729 B=0 |
| Audio (3 pistas) | desfase con el video ≤ 13 ms |
| CPU del proceso | 0,15–0,6 % |
| GPU | motor 3D 0 %, VideoEncode ~9 % (bloque NVENC dedicado, no compite con el juego) |
| Memoria (conjunto de trabajo privado) | ~120 MB con 30 s a 20 Mbps |
| Cierre | limpio en < 100 ms |

## 2. Pruebas manuales con la aplicación

Preparación: publicar en Release (ver README), abrir `%LOCALAPPDATA%\ReplayClip\logs\replayclip.log`
para seguir lo que pasa, y tener abierto un juego en **ventana sin bordes**.

| # | Prueba | Pasos | Resultado esperado |
|---|---|---|---|
| 1 | Arranque | Ejecutar `ReplayClip.exe` | Icono en la bandeja y notificación con los atajos. En el registro: GPU elegida y codificador de hardware |
| 2 | Instancia única | Ejecutarlo otra vez | Mensaje "ya se está ejecutando" |
| 3 | Guardar repetición | Jugar 40 s, pulsar `Ctrl+Shift+F9` | Notificación "Clip guardado" con la ruta; clic abre el Explorador con el archivo seleccionado |
| 4 | Contenido del clip | Abrir el MP4 en Películas y TV y en VLC | Dura ~30 s, se ve fluido, se oye el juego. En VLC: *Audio → Pista* muestra 3 pistas si hay micrófono |
| 5 | Búfer corto | Reiniciar la app y guardar a los 10 s | Clip de ~10 s (lo que haya), sin error |
| 6 | Grabación | `Ctrl+Shift+F10`, jugar 2 min, `Ctrl+Shift+F10` | Icono rojo mientras graba; notificación "Grabación guardada"; MP4 de ~2 min reproducible |
| 7 | Alt-tab | Grabar mientras se cambia entre juego y escritorio | Modo monitor: se ve todo; modo proceso: imagen congelada mientras el juego está minimizado |
| 8 | Ventana cerrada | Modo proceso, cerrar el juego y volver a abrirlo | Mientras está cerrado, imagen negra; al reabrirlo se vuelve a capturar solo (≤1 s) |
| 9 | Cambio de resolución | Cambiar la resolución del juego o del escritorio | La captura continúa; si cambia la proporción aparecen bandas negras |
| 10 | Pérdida del dispositivo | `Win+Ctrl+Shift+B` (reinicia el driver de video) | En el registro: "La cadena de video dejó de funcionar; se recrea". A los ~2 s vuelve a grabar. Un clip guardado justo después contiene solo lo posterior al reinicio |
| 11 | UAC | Abrir algo que pida permisos de administrador | Desktop Duplication se recupera sola tras cerrar el aviso |
| 12 | Sin micrófono | Activar micrófono en Ajustes con ninguno conectado | Aviso "Micrófono no encontrado"; los clips tienen una sola pista |
| 13 | Micrófono desconectado | Desconectarlo mientras graba | La grabación sigue; la pista del micrófono queda en silencio. Al reconectarlo vuelve en ≤2 s |
| 14 | Cambio de salida de audio | Cambiar de altavoces a auriculares | En el registro: "Cambió el dispositivo de salida predeterminado"; el audio del sistema sigue grabándose |
| 15 | Disco lleno | Elegir como carpeta de salida una memoria USB casi llena y grabar | Notificación de error clara; la grabación se detiene sola y lo grabado se puede reproducir. Ningún `.mp4` corrupto |
| 16 | Cierre a mitad de grabación | Grabar y elegir *Salir* en la bandeja | El archivo queda terminado y reproducible |
| 17 | Cierre forzado | Grabar y matar el proceso desde el Administrador de tareas; volver a abrir la app | La grabación aparece como `..._recuperado.mp4` y se reproduce |
| 18 | Atajo ocupado | Asignar un atajo que use otra app | Aviso "Atajo ocupado" |
| 19 | Ajustes | Cambiar fps, bitrate, duración y carpeta; guardar | Se aplican sin reiniciar la app; el siguiente clip refleja los cambios |
| 20 | Inicio con Windows | Activarlo y reiniciar sesión | La app arranca en la bandeja sin notificación de bienvenida |
| 21 | Antitrampas | Jugar una partida de League of Legends con Vanguard y la app activa | Vanguard no la bloquea; el clip se guarda. Usar modo monitor o "sin bordes" |

## 3. Medir el consumo

### Script incluido

Con la app en marcha y jugando:

```powershell
.\tools\Measure-ReplayClip.ps1 -Seconds 120 -CsvPath "$env:TEMP\medicion.csv"
```

Toma una muestra por segundo y muestra el promedio y el máximo de:

* **CpuPct**: CPU del proceso normalizada al total de núcleos (igual que el Administrador de tareas). Objetivo < 3 %.
* **MemoriaMB**: conjunto de trabajo privado (columna "Memoria" del Administrador de tareas). Objetivo < 200 MB.
* **Gpu3dPct / GpuVideoProcPct / GpuCopyPct**: uso de esos motores de la GPU por la app. Objetivo de "GPU extra": < 5 % sumando 3D, VideoProcessing y Copy.
* **GpuEncodePct**: el codificador de video (NVENC/VCN/Quick Sync) es un bloque de hardware separado que el juego no usa; se informa aparte.

### Comprobación con herramientas de Windows

* **Administrador de tareas → Detalles**: añadir las columnas *CPU*, *Memoria (conjunto de trabajo privado activo)*,
  *GPU* y *Motor de GPU*. El motor debe indicar *Video Encode* (y *3D* casi a 0).
* **Administrador de tareas → Rendimiento → GPU**: las gráficas *Video Encode* y *3D* muestran el reparto.
* **Comparar FPS del juego** con y sin ReplayClip (contador de FPS del propio juego o PresentMon):
  la diferencia debería estar dentro del ruido de medición.

### Fugas de memoria

La prueba de resistencia (`--soak-minutes 60`) imprime la memoria cada 5 s y guarda un clip cada 5 minutos.
Con el búfer lleno, la memoria debe quedar estable (±10 MB) durante toda la hora. Las texturas de GPU
se pueden vigilar en el Administrador de tareas (*Memoria de GPU dedicada* del proceso), que tampoco debe crecer.
