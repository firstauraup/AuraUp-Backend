# AuraUpBack

Backend .NET para vigilar cuentas, ordenar posts por rendimiento, detectar outliers y lanzar investigaciones desde el panel admin.

La idea del proyecto es simple:

- guardar cuentas
- leer contenido
- calcular que post rinde mejor
- marcar outliers
- vigilar cuentas con un worker
- mostrar todo eso en el front admin

## Como esta organizado

### `Domain`

Piensalo como la parte que sabe las reglas del negocio.

Aqui viven cosas como:

- que es una cuenta vigilada
- que es un post
- como se marca un outlier
- que datos tiene una alerta

Objetivo:

- que la logica importante no dependa ni de Instagram ni de Playwright ni del front

Ejemplo simple:

- un post con muchas mas views que el promedio termina marcado como `IsOutlier = true`

Ejemplo de data que usa esta capa:

```json
{
  "handle": "velocitygarage",
  "averageViews": 42000,
  "postViews": 410000,
  "outlierMultiplier": 9.76,
  "isOutlier": true
}
```

Que devuelve esta idea:

- reglas claras para saber si un post es normal o es extraordinario

### `Application`

Piensalo como la capa que conecta acciones del sistema con la logica.

Aqui viven:

- comandos
- queries
- handlers

Objetivo:

- decirle al sistema "registra esta cuenta", "inspecciona esta cuenta", "trae el dashboard", "transcribe este post"

Ejemplos:

- `RegisterTrackedAccountCommand`
- `InspectTrackedAccountCommand`
- `GetWatchlistDashboardQuery`

Ejemplo simple explicado como a un amigo:

- `RegisterTrackedAccountCommand`
  - recibe un `handle`, un prompt y cada cuantos minutos vigilar
  - guarda o actualiza la cuenta
- `InspectTrackedAccountCommand`
  - le dice al modulo de Instagram "traeme lo visible de esta cuenta"
  - luego guarda posts, recalcula outliers y crea alertas si hace falta
- `GetWatchlistDashboardQuery`
  - arma la pantalla principal del admin
  - devuelve resumen de cuentas, alerts y mejores posts

### `Infrastructure`

Esta es la capa que hace el trabajo sucio de verdad.

Aqui viven:

- persistencia en archivos JSON
- proveedores de Instagram
- servicio de transcripcion mock
- worker de monitoreo

Objetivo:

- conectar la app con el mundo exterior sin ensuciar las reglas del negocio

Piensalo asi:

- si mañana cambias JSON por PostgreSQL, esta capa cambia
- si mañana cambias RPA por API oficial, esta capa cambia
- el resto del sistema casi no deberia enterarse

## Modulo de Instagram

Este es el modulo mas importante para lo que quieres.

La app ahora esta preparada para cambiar de proveedor sin romper todo.

Piensalo asi:

- la app pide: "inspecciona una cuenta"
- no le importa si eso sale de mock, de RPA o de una API oficial

## Modulo `Instagram Connection`

Este modulo es el que se encarga del login real cuando usas `Rpa`.

Que hace:

- guarda el usuario de Instagram
- guarda la contraseña cifrada
- intenta hacer login automatico con Playwright
- guarda la sesion del navegador
- si la sesion se vence, intenta loguearse otra vez con las credenciales guardadas

Objetivo:

- que el sistema pueda entrar solo a Instagram sin pedirte login manual todo el tiempo

Piezas principales:

- `InstagramConnection`
  - representa la conexion guardada
- `IInstagramConnectionAutomation`
  - contrato del flujo de login/relogin
- `InstagramConnectionAutomation`
  - implementacion real con Playwright
- `InstagramCredentialVault`
  - cifra y descifra la contraseña

Ejemplo mental:

1. guardas `usuario + contraseña`
2. el sistema intenta entrar
3. si entra, guarda `instagram-rpa-session.json`
4. cuando inspeccionas cuentas, reutiliza esa sesion
5. si la sesion muere, vuelve a loguearse solo

### Proveedores disponibles

#### `Mock`

Que hace:

- genera cuentas y posts de prueba muy realistas

Que devuelve:

- nombre de cuenta
- bio
- followers
- posts con caption, views, likes, comments y fecha

Para que sirve:

- probar el panel y la logica sin depender de Instagram

Ejemplo de salida:

```json
{
  "handle": "apexconditioning",
  "displayName": "Apex Conditioning",
  "followersCount": 181244,
  "posts": [
    {
      "externalId": "apexconditioning-reel-01",
      "views": 412000,
      "likes": 24800,
      "comments": 1210
    }
  ]
}
```

#### `Rpa`

Que hace:

- abre Instagram como si fuera un usuario normal usando Playwright
- intenta reutilizar una sesion guardada
- si hace falta, vuelve a loguearse con las credenciales guardadas
- usa el buscador de Instagram
- entra al perfil objetivo
- lee datos visibles
- intenta sacar lista de posts y datos basicos de cada post

Que devuelve:

- datos del perfil
- followers visibles
- posts visibles
- caption
- likes/comments/views cuando se puedan leer
- fechas visibles

Objetivo:

- arrancar con una integracion simple y de solo lectura
- si luego falla o quieres algo mas robusto, se cambia el proveedor sin rehacer todo

Ejemplo mental de lo que hace:

1. revisa si la sesion actual sigue viva
2. si no sigue viva, intenta relogin con usuario y contraseña guardados
3. entra a Instagram
4. busca el handle en el buscador
5. abre ese perfil
6. saca links de reels/posts visibles
7. entra a cada post
8. intenta leer caption, fecha y metricas visibles
9. devuelve ese paquete al sistema

### Como cambiar el proveedor

En `appsettings.json`:

```json
"Instagram": {
  "Provider": "Mock"
}
```

Valores:

- `Mock`
- `Rpa`

## Configuracion del RPA

En `appsettings.json`:

```json
"Instagram": {
  "Provider": "Rpa",
  "CredentialEncryptionKey": "change-this-instagram-credential-key",
  "RpaSessionStatePath": "App_Data/instagram-rpa-session.json",
  "RpaHeadless": true,
  "RpaMaxPosts": 12,
  "LoginTimeoutSeconds": 45,
  "AllowPublicProfileReadWithoutSession": true
}
```

Que significa cada cosa, explicado facil:

- `Provider`
  - dice si vamos a usar mock o rpa

- `RpaSessionStatePath`
  - es el archivo JSON donde guardarias la sesion del navegador
  - sirve para que el RPA entre ya logueado si hace falta

- `CredentialEncryptionKey`
  - es la clave usada para cifrar la contraseña de Instagram
  - no la dejes con el valor por defecto en produccion

- `RpaHeadless`
  - `true` = corre escondido
  - `false` = ves el navegador

- `RpaMaxPosts`
  - cuantos posts intenta leer por cuenta

- `LoginTimeoutSeconds`
  - cuantos segundos espera durante el login automatico

- `AllowPublicProfileReadWithoutSession`
  - si es `true`, intenta leer perfiles publicos aunque no tenga sesion guardada

## Endpoints del modulo de Instagram

### `POST /api/integrations/instagram/connect`

Para que sirve:

- guardar usuario y contraseña
- intentar el login real
- persistir la sesion

Ejemplo de entrada:

```json
{
  "username": "tu_usuario_instagram",
  "password": "tu_password_instagram"
}
```

Que devuelve:

- estado de la conexion
- ruta de la sesion
- si hay credenciales guardadas
- ultimo error si hubo problema

### `POST /api/integrations/instagram/reconnect`

Para que sirve:

- forzar un relogin usando las credenciales guardadas

### `GET /api/integrations/instagram`

Para que sirve:

- ver el estado actual del login de Instagram
- saber si hay sesion activa o si hace falta reconnect

Ejemplo:

```json
{
  "provider": "Rpa",
  "username": "miusuario",
  "status": "Connected",
  "hasStoredCredentials": true,
  "sessionStatePath": "/ruta/completa/App_Data/instagram-rpa-session.json",
  "sessionStateExists": true,
  "lastLoginAtUtc": "2026-03-24T18:00:00Z",
  "lastValidatedAtUtc": "2026-03-24T18:05:00Z",
  "lastError": "",
  "headless": true,
  "maxPosts": 12,
  "allowPublicProfileReadWithoutSession": true
}
```

## Modulo de monitoreo

### `MonitoringBackgroundService`

Que hace:

- revisa las cuentas habilitadas
- mira si ya toca volver a inspeccionarlas
- dispara una nueva inspeccion

Objetivo:

- vigilar cuentas automaticamente
- detectar outliers nuevos sin hacerlo manualmente

Piensalo asi:

- si una cuenta dice `CheckEveryMinutes = 60`
- el worker espera
- cuando toca, la inspecciona otra vez
- si sale un outlier, se genera alerta

Ejemplo de alerta:

```json
{
  "handle": "velocitygarage",
  "postExternalId": "velocitygarage-reel-01",
  "severity": "High",
  "message": "This reel is performing 11.4x above the account average."
}
```

## Modulo de transcripcion

La transcripcion real usa `ClipTranscribeVideoTranscriptionService` con Playwright.

### Variables de entorno de ClipTranscribe

No pongas la cuenta en `appsettings.json`. Configurala en variables de entorno:

```bash
Transcription__ClipTranscribeEmail=tu-email@dominio.com
Transcription__ClipTranscribePassword=tu-password
Transcription__ClipTranscribeSessionStatePath=/app/App_Data/cliptranscribe-rpa-session.json
```

Tambien se aceptan estos alias simples:

```bash
CLIPTRANSCRIBE_EMAIL=tu-email@dominio.com
CLIPTRANSCRIBE_PASSWORD=tu-password
CLIPTRANSCRIBE_SESSION_STATE_PATH=/app/App_Data/cliptranscribe-rpa-session.json
```

Si ya tienes una sesion exportada de Playwright, puedes cargarla por variable:

```bash
CLIPTRANSCRIBE_SESSION_STATE_JSON='{"cookies":[],"origins":[]}'
CLIPTRANSCRIBE_SESSION_STATE_BASE64=eyJjb29raWVzIjpbXSwib3JpZ2lucyI6W119
```

Que registra en logs:

- cuando arranca la transcripcion
- si carga sesion por token/storage state
- cuando inicia y completa login
- cuando pega el link del reel
- cuando envia el link y ClipTranscribe empieza a generar
- cuando captura el texto y cuantos caracteres obtuvo
- cuando cae al fallback de Instagram/caption

Que hace:

- abre ClipTranscribe con Playwright
- reutiliza la sesion guardada si existe
- si no hay sesion y hay credenciales, inicia sesion con la cuenta configurada
- pega la URL del reel
- espera el transcript
- si ClipTranscribe falla, intenta leer metadata publica de Instagram y luego usa el caption como ultimo fallback

Objetivo:

- tener una transcripcion real sin meter credenciales en el repo
- dejar trazas suficientes para diagnosticar si falla login, sesion, pegado de link, generacion o captura de texto

Ejemplo de salida:

```text
Hook: 3 reasons your bench press has been stuck for months...
Main point: the creator sets up a clear promise, adds one concrete example, then closes with a short call to action.
Visual pacing: fast cuts in the first 2 seconds...
Source: https://instagram.com/...
```

Que objetivo cumple:

- que el usuario del admin pueda abrir un post y leer rapido de que trata
- preparar el flujo para luego conectar una transcripcion real

## Datos que guarda la app

Hoy se guarda en JSON.

Archivo principal:

- `App_Data/aura-up-back.json`

Que guarda:

- cuentas
- posts
- alertas
- exploraciones

Ejemplo muy simplificado:

```json
{
  "trackedAccounts": [
    {
      "id": "0d3646ef-cd6f-4ff4-b65d-a1ed4ab7fe4b",
      "handle": "maison_elevate",
      "monitoringEnabled": true
    }
  ],
  "alerts": [
    {
      "id": "3b17b48d-b685-4ad0-80c6-198ab9a9927d",
      "severity": "High"
    }
  ]
}
```

Objetivo:

- tener algo facil de probar
- luego cambiarlo por PostgreSQL sin tocar la logica del dominio

## Flujo simple de uso

Imagina que se lo explico a un amigo:

1. agregas una cuenta
2. la inspeccionas
3. el sistema trae posts y metricas
4. calcula cuales son los outliers
5. los muestra arriba
6. puedes seleccionar posts para investigar
7. puedes transcribirlos
8. si activas monitoreo, el worker la revisa solo

## Endpoints principales explicados simple

### `POST /api/auth/login`

Para que sirve:

- entrar al admin

Ejemplo de entrada:

```json
{
  "username": "admin",
  "password": "ChangeMe123!"
}
```

Ejemplo de salida:

```json
{
  "accessToken": "token...",
  "username": "admin",
  "role": "admin",
  "expiresAtUtc": "2026-03-24T22:00:00Z"
}
```

### `POST /api/accounts`

Para que sirve:

- registrar una cuenta o actualizarla

Ejemplo de entrada:

```json
{
  "handle": "velocitygarage",
  "monitoringPrompt": "Find outlier reels and strongest hooks",
  "monitoringEnabled": true,
  "checkEveryMinutes": 60
}
```

Que devuelve:

- la cuenta guardada con su `id`

### `POST /api/accounts/{accountId}/inspect`

Para que sirve:

- leer perfil y posts usando el proveedor activo

Que devuelve:

- resumen de la inspeccion
- cuenta actualizada
- posts guardados
- alertas creadas si hay outliers fuertes

### `GET /api/accounts/{accountId}`

Para que sirve:

- abrir la pagina de una cuenta en el admin

Que devuelve:

- datos de la cuenta
- posts
- resumen
- transcript si existe

### `GET /api/dashboard/watchlist`

Para que sirve:

- llenar `Overview` y `Monitoring`

Que devuelve:

- cuentas
- mejores métricas
- alertas recientes

### `POST /api/accounts/{accountId}/posts/{postId}/transcribe`

Para que sirve:

- sacar un transcript de ejemplo del post seleccionado

Que devuelve:

- el post actualizado con transcript

### `GET /api/integrations/instagram`

Para que sirve:

- ver rapido si estas en `Mock` o `Rpa`
- ver si la conexion esta `Connected`, `ReconnectRequired` o `Failed`
- ver si el backend tiene credenciales guardadas

## Arranque rapido

```bash
cd /Users/yadielmontalvan/Desktop/AuraUpBack
dotnet build
dotnet run --project src/AuraUpBack.Api
```

Worker:

```bash
dotnet run --project src/AuraUpBack.Worker
```

## Docker para Railway y RPA

Deje un `Dockerfile` en la raiz pensado para este backend.

Que hace:

- compila el API en `linux-x64`
- publica self-contained para no depender del runtime .NET del contenedor final
- usa imagen Playwright para que Chromium y sus dependencias ya existan
- crea `/app/App_Data` para el JSON de datos y la sesion del RPA

### Build local

```bash
docker build -t auraupback .
```

### Run local con RPA

```bash
docker run --rm -p 8080:8080 \
  -e Instagram__Provider=Rpa \
  -e Instagram__RpaHeadless=true \
  -e Instagram__CredentialEncryptionKey=change-this-key \
  -e AdminAuth__Username=admin \
  -e AdminAuth__Password=ChangeMe123! \
  -e AdminAuth__SigningKey=change-this-before-production \
  -v $(pwd)/App_Data:/app/App_Data \
  auraupback
```

### Que debes montar en `/app/App_Data`

- `aura-up-back.json`
  - base de datos JSON del proyecto
- `instagram-rpa-session.json`
  - sesion guardada del navegador si necesitas entrar autenticado

### Variables utiles en Railway

- `Instagram__Provider=Rpa`
- `Instagram__CredentialEncryptionKey=change-this-key`
- `Instagram__RpaHeadless=true`
- `Instagram__RpaMaxPosts=12`
- `Instagram__AllowPublicProfileReadWithoutSession=true`
- `AuraUpBack__DataPath=/app/App_Data/aura-up-back.json`
- `Instagram__RpaSessionStatePath=/app/App_Data/instagram-rpa-session.json`
- `AdminAuth__Username=admin`
- `AdminAuth__Password=...`
- `AdminAuth__SigningKey=...`

### Nota practica

Si Railway no tiene volumen persistente, el JSON de datos y la sesion del RPA se perderan cuando el contenedor reinicie.

Para produccion o pruebas serias:

- monta un volumen persistente en `/app/App_Data`
- o cambia luego la persistencia de JSON a PostgreSQL

### Resumen real para Railway

Lo que si va en git:

- `Dockerfile`
- `railway.json`
- el codigo del backend que guarda y reutiliza `instagram-rpa-session.json`

Lo que no se resuelve con git:

- crear el volumen persistente en Railway
- montarlo exactamente en `/app/App_Data`
- subir al volumen el archivo `instagram-rpa-session.json` si la sesion se genero fuera de Railway

Sin ese volumen, Railway redeploya o reinicia el contenedor y la sesion se pierde aunque el repo este correcto.

## Login admin

```http
POST /api/auth/login
```

Payload:

```json
{
  "username": "admin",
  "password": "ChangeMe123!"
}
```

El resto de `/api/*` usa:

```http
Authorization: Bearer <token>
```

## Cosas honestas que debes saber

- `Mock` funciona bien para probar visual y logica
- `Rpa` es modular y facil de cambiar, pero depende de como responda Instagram
- el transcript aun no es IA real
- el almacenamiento aun es JSON
- la arquitectura ya esta pensada para cambiar proveedor sin romper todo

## Que haria yo para probarlo hoy

### Caso 1: probar todo rapido con mock

1. deja `"Provider": "Mock"`
2. levanta la API
3. crea una cuenta desde el admin
4. inspeccionala
5. mira feed, outliers, research y monitoring

Objetivo:

- validar el producto y la experiencia completa

### Caso 2: probar lectura simple con RPA

1. cambia `"Provider": "Rpa"`
2. deja `RpaHeadless` en `false` al principio
3. conecta Instagram con usuario y contraseña
4. inspecciona una cuenta publica simple

Objetivo:

- comprobar si el proveedor modular real puede leer datos visibles sin romper el resto del sistema

## Como generar `instagram-rpa-session.json`

Este flujo sigue siendo util como respaldo manual.

La forma correcta es crear la sesion localmente, con navegador visible, y luego reutilizar ese archivo.

### 1. Compila la herramienta

```bash
cd /Users/yadielmontalvan/Desktop/AuraUpBack
dotnet build src/AuraUpBack.RpaSessionTool/AuraUpBack.RpaSessionTool.csproj
```

### 2. Instala Chromium para Playwright si hace falta

macOS o Linux:

```bash
pwsh src/AuraUpBack.RpaSessionTool/bin/Debug/net9.0/playwright.ps1 install chromium
```

Si no tienes `pwsh`, instala PowerShell o usa el script equivalente de Playwright que te deje el paquete en `bin/Debug/net9.0`.

### 3. Abre Instagram y guarda la sesion

```bash
dotnet run --project src/AuraUpBack.RpaSessionTool -- --output App_Data/instagram-rpa-session.json
```

Que hace:

- abre un navegador real
- te deja loguearte manualmente
- espera a que pulses `Enter`
- guarda cookies y storage state en `App_Data/instagram-rpa-session.json`

### 4. Cambia el proveedor a `Rpa`

En `src/AuraUpBack.Api/appsettings.Development.json` o por variables:

```json
{
  "Instagram": {
    "Provider": "Rpa",
    "RpaSessionStatePath": "App_Data/instagram-rpa-session.json",
    "RpaHeadless": false,
    "RpaMaxPosts": 12,
    "AllowPublicProfileReadWithoutSession": true
  }
}
```

### 5. Levanta el API

```bash
dotnet run --project src/AuraUpBack.Api
```

### 6. Prueba una cuenta real

Tambien puedes conectar Instagram directamente desde el backend sin generar la sesion manual primero:

```bash
curl -X POST http://localhost:5000/api/integrations/instagram/connect \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer TU_TOKEN" \
  -d '{
    "username":"tu_usuario_instagram",
    "password":"tu_password_instagram"
  }'
```

Si el login sale bien, el backend guarda la sesion y ya no necesitas volver a pasar usuario y contraseña hasta que la sesion falle.

Primero haz login admin:

```bash
curl -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"ChangeMe123!"}'
```

Luego registra la cuenta:

```bash
curl -X POST http://localhost:5000/api/accounts \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer TU_TOKEN" \
  -d '{
    "handle":"instagram",
    "monitoringPrompt":"Find top outliers and strongest hooks",
    "monitoringEnabled":true,
    "checkEveryMinutes":60
  }'
```

Luego inspecciona la cuenta con el `accountId` devuelto:

```bash
curl -X POST http://localhost:5000/api/accounts/ACCOUNT_ID/inspect \
  -H "Authorization: Bearer TU_TOKEN"
```

### 7. Verifica el proveedor activo

```bash
curl http://localhost:5000/api/integrations/instagram \
  -H "Authorization: Bearer TU_TOKEN"
```

Deberias ver algo asi:

```json
{
   "provider": "Rpa",
   "username": "tu_usuario_instagram",
   "status": "Connected",
   "hasStoredCredentials": true,
   "sessionStatePath": "/ruta/.../App_Data/instagram-rpa-session.json",
   "sessionStateExists": true,
   "headless": false,
   "maxPosts": 12,
   "allowPublicProfileReadWithoutSession": true
}
```

### Si luego quieres usar esa sesion en Railway

Haz esto:

1. genera el archivo localmente
2. subelo al volumen persistente montado en `/app/App_Data`
3. define:
   - `Instagram__Provider=Rpa`
   - `Instagram__RpaSessionStatePath=/app/App_Data/instagram-rpa-session.json`

La razon es simple:

- Railway no es un lugar comodo para hacer login interactivo con 2FA
- la sesion se genera mejor localmente y despues se reutiliza
