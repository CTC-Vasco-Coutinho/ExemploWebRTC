# ExemploWebRTC — Streaming de Vídeo com WebRTC na Unity

Projeto de exemplo que demonstra como receber um stream de vídeo RTSP dentro da Unity Engine utilizando **WebRTC** e o protocolo **WHEP** (WebRTC-HTTP Egress Protocol), com o servidor [MediaMTX](https://github.com/bluenviron/mediamtx) como ponte de sinalização.

---

## Visão Geral

| Item | Descrição |
|---|---|
| **Engine** | Unity 6000.0.71f1 (Unity 6) |
| **Render Pipeline** | Universal Render Pipeline (URP) |
| **Pacote WebRTC** | `com.unity.webrtc` v3.0.0 |
| **Servidor de mídia** | MediaMTX (RTSP → WebRTC) |
| **Protocolo de sinalização** | WHEP (POST com SDP) |
| **Plataforma alvo** | Standalone (Linux/Windows/macOS) |

---

## Conceitos Explicados

### 1. WebRTC (Web Real-Time Communication)

WebRTC é um padrão aberto que permite comunicação em tempo real diretamente entre aplicações — voz, vídeo e dados — sem necessidade de plugins ou servidores intermediários de mídia. A Unity oferece suporte através do pacote oficial `com.unity.webrtc`.

**Componentes principais usados no projeto:**

- **`RTCPeerConnection`** — representa uma conexão ponto a ponto. Gerencia a negociação SDP, candidatos ICE e o fluxo de mídia.
- **`RTCConfiguration`** — define parâmetros da conexão, como servidores STUN/TURN. Neste projeto, `iceServers` é deixado vazio (`new RTCIceServer[] { }`) para forçar o tráfego a permanecer 100% na rede local (192.168.x.x), eliminando dependência de servidores externos.
- **`VideoStreamTrack`** — representa uma trilha de vídeo recebida remotamente. O evento `OnVideoReceived` entrega uma `Texture` que pode ser atribuída diretamente a um `RawImage` da UI.
- **`RTCRtpTransceiver`** — combinado send/receive de mídia. Aqui configurado como `RecvOnly` (apenas recebimento de vídeo).

### 2. SDP (Session Description Protocol)

O SDP é um formato de texto puro que descreve os parâmetros de uma sessão multimídia: codecs suportados, endereços IP, portas, tipo de mídia (áudio/vídeo), etc.

No WebRTC, a negociação segue o modelo **Offer/Answer**:

```
Unity (cliente)                      MediaMTX (servidor)
     │                                      │
     │  (1) CreateOffer()                   │
     │  ──────────────────────────────→     │
     │  POST /caminho  Content-Type:        │
     │  application/sdp  [SDP Offer]        │
     │                                      │
     │  (2) ←────────────────────────────   │
     │  HTTP 200  [SDP Answer]              │
     │                                      │
     │  (3) SetRemoteDescription(answer)    │
     │                                      │
     │  ←═══════ Fluxo de Vídeo ═══════→   │
```

1. O cliente cria uma **Offer** descrevendo suas capacidades (codecs, transceivers).
2. Envia ao servidor via HTTP POST (WHEP).
3. O servidor responde com uma **Answer**.
4. Ambos configuram as descrições locais e remotas (`SetLocalDescription` / `SetRemoteDescription`).

### 3. ICE (Interactive Connectivity Establishment)

ICE é o mecanismo que descobre o melhor caminho de rede entre dois pares. Ele funciona assim:

1. **Coleta de candidatos**: cada lado coleta todos os endereços IP e portas possíveis (endereços locais, endereço público via STUN, relays via TURN).
2. **Verificação de conectividade**: os pares são testados em ordem de prioridade até encontrar um que funcione.
3. **Estabelecimento**: o par funcional é usado para o tráfego de mídia.

**Trickle ICE vs ICE tradicional:**

- **Trickle ICE**: candidatos são enviados incrementalmente, assim que descobertos — acelera a conexão, mas requer um canal de sinalização bidirecional (WebSocket, por exemplo).
- **ICE tradicional**: todos os candidatos são enviados de uma vez, embutidos no SDP.

> ⚠️ **Importante**: O protocolo WHEP **não suporta trickle ICE** (é uma única requisição HTTP POST, sem canal persistente). Por isso o script aguarda o `GatheringState` atingir `Complete` (com timeout de 10 segundos) antes de enviar o SDP.

### 4. WHEP (WebRTC-HTTP Egress Protocol)

WHEP é um padrão IETF ([RFC 8835](https://datatracker.ietf.org/doc/rfc8835/)) que define como negociar uma sessão WebRTC usando apenas HTTP. O fluxo é extremamente simples:

```
POST http://<servidor>:8889/<nome-do-stream>
Content-Type: application/sdp

v=0
o=- 123456789 2 IN IP4 127.0.0.1
s=-
t=0 0
... (SDP Offer completo)
```

O servidor responde com o **Answer SDP** no corpo da resposta HTTP 200. Pronto — sem WebSocket, sem long polling, sem mensagens em duas direções. Uma única requisição resolve a negociação.

**Vantagens do WHEP:**

- Extremamente simples de implementar (um POST HTTP).
- Funciona através de proxies e balanceadores de carga HTTP.
- Ideal para cenários onde o cliente só consome mídia (não publica).

### 5. MediaMTX

[MediaMTX](https://github.com/bluenviron/mediamtx) (anteriormente conhecido como `rtsp-simple-server`) é um servidor de mídia de código aberto que atua como proxy/ponte entre diversos protocolos:

- **Entrada**: RTSP, RTMP, HLS, WebRTC (WHIP), entre outros.
- **Saída**: RTSP, RTMP, HLS, WebRTC (WHEP).

Neste projeto, ele cumpre dois papéis:

1. **Recebe** o stream RTSP de uma câmera ou fonte externa (ex.: `rtsp://192.168.100.56:8554/mystream`).
2. **Expõe** um endpoint WHEP na porta **8889** para que clientes WebRTC (a Unity) consumam o stream.

**Configuração típica do MediaMTX (`mediamtx.yml`):**

```yaml
rtspAddress: :8554
whepAddress: :8889
paths:
  mystream:
    source: rtsp://camera-ip:554/stream
```

### 6. RTSP (Real-Time Streaming Protocol)

RTSP é um protocolo de controle para streaming de mídia (RFC 2326). Diferente do WebRTC, ele:

- É baseado em **estado** (SETUP, PLAY, TEARDOWN).
- Não possui **NAT traversal** nativo — difícil de usar fora da rede local.
- Opera sobre TCP ou UDP, com canais separados para controle e dados (RTP/RTCP).

O MediaMTX faz a **tradução RTSP → WebRTC**, permitindo que a Unity consuma streams RTSP sem implementar o protocolo RTSP diretamente, aproveitando todas as vantagens do WebRTC (NAT traversal, codecs modernos, baixa latência).

---

## Arquitetura do Projeto

```
┌──────────┐   RTSP    ┌────────────┐   WHEP (HTTP)   ┌──────────┐
│  Câmera  │ ────────→ │  MediaMTX  │ ←────────────── │  Unity   │
│  (RTSP)  │           │  (proxy)   │   POST /stream  │ (WebRTC) │
└──────────┘           │            │   SDP Offer     └──────────┘
                       │  Portas:   │ ───────────────→
                       │  8554 RTSP │   HTTP 200
                       │  8889 WHEP │   SDP Answer
                       └────────────┘
                            │
                       Fluxo de Vídeo
                       (SRTP/UDP direto
                        entre pares)
```

O fluxo completo:

1. A câmera publica o stream RTSP para o MediaMTX.
2. O MediaMTX mantém o stream disponível internamente.
3. A Unity (cliente) faz um POST HTTP para `http://192.168.100.56:8889/mystream` com o SDP Offer.
4. O MediaMTX responde com o SDP Answer.
5. A conexão WebRTC é estabelecida diretamente entre Unity e MediaMTX.
6. Os frames de vídeo fluem via SRTP/UDP.

---

## Estrutura do Código

O arquivo principal é `Assets/WebRTCReceiver.cs`. Abaixo o detalhamento da lógica:

### Campos Serializados (configuráveis no Inspector)

```csharp
[SerializeField] private RawImage displayImage;   // RawImage da UI para renderizar o vídeo
[SerializeField] private string rtspSourceUrl;      // URL RTSP da fonte
[SerializeField] private string whepUrl;            // Endpoint WHEP (opcional — derivado automaticamente)
```

### `Start()`

- Registra o callback `OnTrack`.
- Inicia a corrotina `WebRTC.Update()` — **obrigatório** no WebRTC 3.x para processar quadros e disparar eventos a cada frame.
- Dispara `ConnectToStream()`.

### `ConnectToStream()` — Corrotina principal de negociação

| Etapa | Código | Descrição |
|---|---|---|
| **1. Montar URL WHEP** | `BuildWhepUrlFromRtsp()` | Converte `rtsp://host:8554/path` → `http://host:8889/path`. Se `whepUrl` estiver preenchido, usa ele diretamente. |
| **2. Criar PeerConnection** | `new RTCPeerConnection(ref config)` | Configura `iceServers` vazio (rede local) e adiciona transceiver `RecvOnly` de vídeo. |
| **3. Criar Offer** | `peerConnection.CreateOffer()` | Gera o SDP local com codecs e capacidades. |
| **4. SetLocalDescription** | `peerConnection.SetLocalDescription(ref desc)` | Aplica a offer como descrição local, iniciando o ICE gathering. |
| **5. Aguardar ICE Gathering** | Loop `while (GatheringState != Complete)` | Espera até 10s por todos os candidatos ICE. Necessário porque WHEP não suporta trickle ICE. |
| **6. POST Offer** | `UnityWebRequest.Post(signalingUrl, sdp)` | Envia o SDP como `Content-Type: application/sdp`. |
| **7. Receber Answer** | `www.downloadHandler.text` | Lê o SDP remoto da resposta HTTP. |
| **8. SetRemoteDescription** | `peerConnection.SetRemoteDescription(ref remoteDesc)` | Aplica o SDP do servidor, concluindo a negociação. |
| **9. White-out fade** | `WhiteOutFade()` | Efeito de fade para branco sobre o `RawImage` (1 segundo). |

### `OnTrack(RTCTrackEvent)`

Callback disparado quando o MediaMTX começa a enviar trilhas de mídia. Filtra por `VideoStreamTrack` e vincula `OnVideoReceived` para atualizar a textura:

```csharp
videoTrack.OnVideoReceived += tex =>
{
    displayImage.texture = tex;
};
```

### `BuildWhepUrlFromRtsp(string)`

- Extrai host e caminho da URL RTSP.
- Reconstrói como `http://{host}:8889/{caminho}` (endpoint WHEP padrão do MediaMTX).

### `OnDestroy()`

- Para a corrotina `WebRTC.Update()`.
- Fecha e libera a `RTCPeerConnection` com `Close()` e `Dispose()`.

---

## Configuração Necessária

### No Unity Editor

1. **Player Settings → Other Settings → Allow downloads over HTTP**: defina como **"Always allowed"**.
   > O WHEP usa HTTP (não HTTPS) na rede local. Sem essa configuração, a Unity bloqueia a requisição.

2. O pacote `com.unity.webrtc` 3.0.0 já está listado no `Packages/manifest.json`.

3. Na cena, adicione um **Canvas** com um **RawImage** e um GameObject com o script `WebRTCReceiver`.

### No Inspector (GameObject com WebRTCReceiver)

| Campo | Descrição | Exemplo |
|---|---|---|
| `Display Image` | Arraste o `RawImage` da UI onde o vídeo será exibido. | `Canvas/RawImage` |
| `Rtsp Source Url` | URL do stream RTSP publicado no MediaMTX. | `rtsp://192.168.100.56:8554/mystream` |
| `Whep Url` | Endpoint WHEP manual (opcional). Se vazio, derivado automaticamente. | `http://192.168.100.56:8889/mystream` |

### No Servidor (MediaMTX)

Certifique-se de que o MediaMTX está rodando com as portas padrão:
- **8554**: RTSP (entrada da câmera)
- **8889**: WHEP (saída WebRTC para clientes)

Comando para iniciar o MediaMTX:

```bash
./mediamtx
```

---

## Dependências

| Pacote Unity | Versão | Propósito |
|---|---|---|
| `com.unity.webrtc` | 3.0.0 | API WebRTC nativa para Unity |
| `com.unity.render-pipelines.universal` | 17.0.4 | Pipeline de renderização URP |
| `com.unity.ugui` | 2.0.0 | Sistema de UI (RawImage, Canvas) |

---

## Possíveis Erros e Soluções

| Erro | Causa Provável | Solução |
|---|---|---|
| **404 Not Found** | O stream RTSP não está sendo publicado no MediaMTX no momento. | Verifique se a câmera/fonte está ativa e publicando corretamente para o servidor. |
| **405 Method Not Allowed** | O endpoint WHEP está com formato incorreto. | Preencha `Whep Url` manualmente no Inspector com o caminho exato do stream. |
| **Insecure connection not allowed** | HTTP bloqueado nas Player Settings da Unity. | Em **Player Settings → Other Settings**, altere `Allow downloads over HTTP` para `Always allowed`. |
| **Timeout no ICE Gathering** | Nenhum candidato ICE viável encontrado em 10 segundos. | Verifique conectividade de rede com o servidor MediaMTX. Aumente o `gatheringTimeout` no código se necessário. |
| **Vídeo não aparece (RawImage preta)** | Conexão estabelecida mas sem frames. | Verifique se o stream de origem está realmente enviando vídeo. Confira os logs do MediaMTX. |

---

## Referências

- [Documentação oficial Unity WebRTC](https://docs.unity3d.com/Packages/com.unity.webrtc@3.0/manual/index.html)
- [MediaMTX — GitHub](https://github.com/bluenviron/mediamtx)
- [RFC 8835 — WHEP (WebRTC-HTTP Egress Protocol)](https://datatracker.ietf.org/doc/rfc8835/)
- [WebRTC.org — Padrão e APIs](https://webrtc.org/)
- [RFC 2326 — RTSP](https://datatracker.ietf.org/doc/html/rfc2326)
- [RFC 8445 — ICE](https://datatracker.ietf.org/doc/html/rfc8445)
