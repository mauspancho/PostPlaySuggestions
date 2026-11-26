# Post Play Suggestions para Jellyfin

Plugin para Jellyfin 12.4 que muestra una vista de sugerencias estilo Netflix cuando una
película está por terminar o cuando termina el último episodio de una serie. El reproductor se
minimiza y las sugerencias aparecen en un panel lateral (o inferior en móvil), adaptándose a los
colores del tema activo.

## Instalar desde el repositorio de plugins

1. En Jellyfin abre **Administración → Complementos → Repositorios**.
2. Pega esta URL:

   ```
   https://raw.githubusercontent.com/mauspancho/PostPlaySuggestions/main/manifest.json
   ```

3. Guarda, ve a **Complementos → Catálogo** y busca **Post Play Suggestions**.
4. Instala y reinicia Jellyfin.

## Características

- Detecta el vídeo en reproducción y consulta la sesión actual (`NowPlaying`).
- Se activa **justo cuando empiezan los créditos finales**:
  1. segmento `Outro` de Jellyfin (`/MediaSegments`),
  2. capítulo cuyo nombre indique créditos (ignora "Opening Credits" del inicio),
  3. último capítulo del archivo cuando está en el tramo final (≥90 % del metraje),
  4. estimación por porcentaje del metraje (`FallbackCreditsPercent`) cuando no hay capítulos,
  5. último recurso: `TriggerSecondsBeforeEnd` segundos antes del final.
- Reintenta leer los créditos si la primera consulta falla (token o sesión aún no listos).
- Nunca se activa fuera del reproductor (menú principal, vídeo oculto o ajeno al ítem).
- Películas: sugerencias vía `Items/{id}/Similar`, con respaldo a elementos recientes del mismo tipo.
- Episodios: puede limitarse al último episodio de la última temporada (`ShowOnSeriesFinaleOnly`).
- Minimiza el reproductor (38 % del ancho) y muestra las sugerencias en un panel ancho, con rejilla adaptable de tarjetas.
- Tarjetas con póster, metadatos, botón **Reproducir** y **Detalles**.
- Minimiza el reproductor y restaura la pantalla completa al cerrar.
- Navegación con mando/TV: foco visible y movimiento con las flechas del D-pad.
- Tema dinámico: lee variables CSS y colores computados, y reacciona a cambios de tema con
  `MutationObserver` (variables `--pps-*`).
- Diseño adaptable: panel lateral en escritorio, panel inferior en móvil (con `dvh` y áreas seguras).
- **Apps nativas** (Android TV, iOS, Swiftfin, Findroid, Infuse, Kodi, Roku…): estas apps no pueden
  mostrar el panel web. Al empezar los créditos, el servidor reproduce automáticamente la mejor
  sugerencia en el mismo dispositivo (`NativeAutoPlay`). La app de Android con reproductor web sigue
  mostrando el panel; el servidor lo detecta y no lanza una segunda reproducción.

## Cómo funciona en cada tipo de cliente

| Cliente | Experiencia |
| --- | --- |
| Navegador, Tizen, webOS, Xbox, app Android con reproductor web | Panel lateral/inferior con tarjetas y botones |
| Android TV, iOS, Swiftfin, Findroid, Infuse, Kodi, Roku | Reproducción automática de la sugerencia al empezar los créditos |

El modo nativo se puede desactivar en la configuración del plugin. Solo actúa en sesiones cuyo
cliente se identifica como app nativa; nunca en clientes web/TV web.

## Estructura

```
Jellyfin.Plugin.PostPlaySuggestions.csproj   Proyecto net10.0 (Jellyfin 12.4)
Plugin.cs                                    Entrada del plugin (GUID f5051302-…)
PluginConfiguration.cs                       Enabled, MaxSuggestions, TriggerSecondsBeforeEnd, ShowOnSeriesFinaleOnly, NativeAutoPlay
Api/SuggestionsConfigController.cs           Config, Script, Styles, NowPlaying, Finale, Credits, Panel
Services/PostPlayServiceRegistrator.cs       Registro DI
Services/PostPlayStartupFilter.cs            Registra el middleware al inicio del pipeline
Services/PostPlayInjectionMiddleware.cs      Inyecta el loader en /web/
Services/PostPlayWebInjection.cs             Bloque HTML compartido
Services/CreditsResolver.cs                  Detección compartida de créditos y final de serie
Services/WebPanelRegistry.cs                 Sesiones con panel web activo
Services/PostPlayNativeService.cs            Modo nativo (Android TV, iOS, reproductores nativos)
Web/configPage.html                          Página de administración
Web/postplay-suggestions.js                  Lógica del frontend
Web/postplay-suggestions.css                 Estilos
```

## Compilar

```bash
cd /home/maus/Documentos/PostPlaySuggestions
~/.dotnet/dotnet build -c Release
```

Salida: `bin/Release/net10.0/Jellyfin.Plugin.PostPlaySuggestions.dll`.

## Desplegar en el Jellyfin de Docker

```bash
VERSION=0.3.0.0
mkdir -p "/home/maus/docker/jellyfin/config/data/plugins/PostPlaySuggestions_${VERSION}"
cp bin/Release/net10.0/Jellyfin.Plugin.PostPlaySuggestions.dll \
   "/home/maus/docker/jellyfin/config/data/plugins/PostPlaySuggestions_${VERSION}/"
docker restart jellyfin
```

## Publicar una versión nueva

Cada cambio del plugin debe publicarse como una versión nueva para que Jellyfin pueda
instalarlo o actualizarlo desde el repositorio. El proceso está automatizado en un solo comando:

```bash
./release.sh 0.4.1.0 "Descripción breve de los cambios"
```

El script:

1. actualiza la versión en el `.csproj`, `Plugin.cs` y `Web/postplay-suggestions.js`;
2. compila en Release;
3. empaqueta `artifacts/PostPlaySuggestions_<versión>.zip` con el DLL en la raíz;
4. agrega la versión al principio de `manifest.json` con checksum MD5 y fecha;
5. hace commit y push a `main`;
6. crea el release `v<versión>` con el ZIP adjunto;
7. verifica que el manifest y el asset respondan y que el checksum coincida.

Requisitos: `gh` autenticado, `~/.dotnet/dotnet` (o `DOTNET=/ruta/dotnet`) y `zip`
(o Python 3 como respaldo).

## Verificar

```bash
curl -s http://localhost:8096/PostPlaySuggestions/Config
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8096/PostPlaySuggestions/Script
curl -s http://localhost:8096/web/ | grep -c data-pps-script
```

El último comando debe devolver `1`: significa que el loader se inyectó en la página web.

## Configuración

En el panel de Jellyfin: **Administración → Complementos → Post Play Suggestions**.

| Opción | Descripción | Defecto |
| --- | --- | --- |
| Activar | Activa o desactiva el plugin | Sí |
| Máximo de sugerencias | Tarjetas a mostrar (1–12) | 6 |
| Porcentaje del metraje | Estimación del inicio de créditos cuando no hay segmentos ni capítulos (80–99) | 94 |
| Segundos antes del final | Antelación de respaldo cuando no se detectan créditos (1–300) | 20 |
| Solo al finalizar la serie | Solo último episodio de la última temporada | Sí |
| Continuar en apps nativas | Reproduce la mejor sugerencia en Android TV/iOS/reproductores nativos al empezar los créditos | Sí |

## Notas técnicas

- La inyección se hace por middleware. Premium UI responde `/web/` antes que el middleware
  normal, por lo que `PostPlayStartupFilter` mueve su middleware al inicio del pipeline usando
  el campo privado `_components` de `IApplicationBuilder`. Si ASP.NET Core cambia ese detalle
  interno, la inyección dejaría de funcionar.
- Los endpoints `Config`, `Script` y `Styles` son anónimos; `NowPlaying` y `Finale` requieren
  autenticación.
- El frontend funciona sin AccesoDirecto: usa la sesión web y `ApiClient`.

## Depuración en el navegador

```js
window.PostPlaySuggestions.version      // versión cargada
window.PostPlaySuggestions.getState()   // estado interno
window.PostPlaySuggestions.show({ itemId: '...', itemType: 'Movie', name: '...' })
window.PostPlaySuggestions.close()
```
