# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

Política de firma de código de Capturix. También publicada en
<https://protohub.digital/capturix/firma/>.

## Qué se firma

- Solo los instaladores de Capturix (`CapturixSetup-<versión>.exe`) compilados por GitHub Actions
  a partir del código de este repositorio, en el flujo `.github/workflows/release.yml`.
- Cada firma se aprueba a mano en SignPath antes de publicar la versión.
- Los binarios llevan el nombre de producto `Capturix` y la versión de `Directory.Build.props`.

## Equipo

| Rol | Personas |
|---|---|
| Autores (committers) | [Luis E. Bautista](https://github.com/LuisEBautistaR) |
| Revisores (cambios de terceros, pull requests) | [Luis E. Bautista](https://github.com/LuisEBautistaR) |
| Aprobadores de firma | [Luis E. Bautista](https://github.com/LuisEBautistaR) |

Todos los miembros usan autenticación de dos factores en GitHub y en SignPath.

## Privacidad

Este programa no transfiere ninguna información a otros sistemas de red a menos que lo pida
expresamente el usuario o la persona que lo instala. Las capturas se guardan en la carpeta que el
usuario elige; si elige su carpeta de OneDrive o de Google Drive, la sube la aplicación de ese
servicio, no Capturix. El reconocimiento de texto (OCR) usa el motor de Windows y funciona sin
conexión.

## Instalación y desinstalación

El instalador se instala por usuario, sin permisos de administrador, y no cambia la configuración
del sistema salvo lo que el usuario marque (acceso en el escritorio, iniciar con Windows). Se
desinstala desde Configuración de Windows o con «Desinstalar Capturix» en el menú Inicio.
