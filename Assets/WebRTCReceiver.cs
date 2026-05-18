using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Unity.WebRTC;
using UnityEngine.Networking;

public class WebRTCReceiver : MonoBehaviour
{
    // Arraste sua RawImage do Inspector para cá para desenhar o vídeo
    [SerializeField] private RawImage displayImage;

    // Origem RTSP publicada no MediaMTX.
    [SerializeField] private string rtspSourceUrl = "rtsp://192.168.100.56:8554/mystream";

    // Endpoint WHEP para negociar o WebRTC.
    // Se ficar vazio, o script tenta derivar automaticamente a partir da URL RTSP.
    [SerializeField] private string whepUrl = "";

    private RTCPeerConnection peerConnection;
    private DelegateOnTrack onTrackDelegate;
    private Coroutine webRtcUpdateCoroutine;

    void Start()
    {
        onTrackDelegate = OnTrack;
        // Obrigatorio no WebRTC 3.x: processa frames e dispara OnVideoReceived a cada frame
        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
        StartCoroutine(ConnectToStream());
    }

    private IEnumerator ConnectToStream()
    {
        var signalingUrl = string.IsNullOrWhiteSpace(whepUrl)
            ? BuildWhepUrlFromRtsp(rtspSourceUrl)
            : whepUrl;

        if (string.IsNullOrWhiteSpace(signalingUrl))
        {
            Debug.LogError("Nao foi possivel montar a URL WHEP. Configure whepUrl manualmente no Inspector.");
            yield break;
        }

        // Substitua o bloco antigo por este:
        var configuration = new RTCConfiguration
        {
            // Deixe vazio! Força o tráfego a ficar 100% dentro da sua rede local (192.168.x.x)
            iceServers = new RTCIceServer[] { } 
        };

        peerConnection = new RTCPeerConnection(ref configuration);

        peerConnection.AddTransceiver(TrackKind.Video, new RTCRtpTransceiverInit
        {
            direction = RTCRtpTransceiverDirection.RecvOnly
        });

        peerConnection.OnTrack = onTrackDelegate;
        peerConnection.OnIceConnectionChange = state => {Debug.Log($"Status da Conexão de Vídeo (ICE): {state}");};
        var opOffer = peerConnection.CreateOffer();
        yield return opOffer;

        if (opOffer.IsError)
        {
            Debug.LogError($"Erro ao criar Offer: {opOffer.Error.message}");
            yield break;
        }

        var localDesc = opOffer.Desc;
        var opLocalDesc = peerConnection.SetLocalDescription(ref localDesc);
        yield return opLocalDesc;

        if (opLocalDesc.IsError)
        {
            Debug.LogError($"Erro ao definir LocalDescription: {opLocalDesc.Error.message}");
            yield break;
        }

        // Aguarda o ICE gathering terminar para que todos os candidatos locais
        // sejam embutidos no SDP antes de enviar ao MediaMTX (WHEP nao suporta trickle ICE).
        float gatheringTimeout = 10f;
        float gatheringElapsed = 0f;
        while (peerConnection.GatheringState != RTCIceGatheringState.Complete)
        {
            gatheringElapsed += Time.deltaTime;
            if (gatheringElapsed >= gatheringTimeout)
            {
                Debug.LogWarning("Timeout aguardando ICE gathering. Enviando SDP com candidatos parciais.");
                break;
            }
            yield return null;
        }

        Debug.Log($"ICE Gathering concluido ({peerConnection.GatheringState}). Enviando SDP com candidatos locais.");

        // Usa o LocalDescription atualizado, que ja contem os candidatos ICE coletados
        string localSdp = peerConnection.LocalDescription.sdp;
        byte[] rawFormData = System.Text.Encoding.UTF8.GetBytes(localSdp);

        using (UnityWebRequest www = new UnityWebRequest(signalingUrl, UnityWebRequest.kHttpVerbPOST))
        {
            www.uploadHandler = new UploadHandlerRaw(rawFormData);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/sdp");

            Debug.Log($"Negociando WebRTC via WHEP: {signalingUrl} (source RTSP: {rtspSourceUrl})");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                string responseBody = www.downloadHandler?.text;
                Debug.LogError($"Erro de comunicacao com o MediaMTX: {www.error} | HTTP {www.responseCode} | Resposta: {responseBody}");

                if (www.responseCode == 404)
                {
                    Debug.LogError(
                        $"404 Not Found: o stream '{rtspSourceUrl}' nao esta sendo publicado no MediaMTX no momento. " +
                        "Verifique se a camera/fonte esta ativa e publicando para o servidor antes de conectar.");
                }
                else if (www.responseCode == 405)
                {
                    Debug.LogError(
                        "405 Method Not Allowed: o endpoint WHEP esta com formato errado. " +
                        "Tente preencher 'Whep Url' manualmente no Inspector com o caminho exato.");
                }
                else if (www.error != null && www.error.IndexOf("Insecure connection not allowed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.LogError("HTTP inseguro bloqueado: Em Player Settings > Other Settings, altere 'Allow downloads over HTTP' para 'Always allowed'.");
                }
                yield break;
            }

            string remoteSdp = www.downloadHandler.text;

            var remoteDesc = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = remoteSdp
            };

            var opRemoteDesc = peerConnection.SetRemoteDescription(ref remoteDesc);
            yield return opRemoteDesc;

            if (opRemoteDesc.IsError)
            {
                Debug.LogError($"Erro ao definir RemoteDescription: {opRemoteDesc.Error.message}");
                yield break;
            }
        }

        Debug.Log("Negociacao WebRTC concluida. Aguardando frames...");
        yield return StartCoroutine(WhiteOutFade());
    }

    private static string BuildWhepUrlFromRtsp(string rtspUrl)
    {
        if (string.IsNullOrWhiteSpace(rtspUrl))
            return string.Empty;

        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var sourceUri))
            return string.Empty;

        if (!string.Equals(sourceUri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var path = sourceUri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        // No endpoint WHEP padrao do MediaMTX, o POST vai direto para o path na porta 8889.
        return $"http://{sourceUri.Host}:8889/{path}";
    }

    // Essa função é disparada automaticamente quando o MediaMTX começa a enviar os bytes de vídeo
    private void OnTrack(RTCTrackEvent e)
    {
        if (e.Track is VideoStreamTrack videoTrack)
        {
            // Vincula o fluxo de vídeo que está chegando à nossa textura/RawImage na Unity
            videoTrack.OnVideoReceived += tex =>
            {
                displayImage.texture = tex;
            };
        }
    }

    private IEnumerator WhiteOutFade()
    {
        // Define a cor inicial como branca (ou a cor atual se quiser manter a lógica original)
        // Aqui vamos forçar branco para o efeito solicitado
        Color startColor = displayImage.color; // Pode ser Color.white se quiser começar do branco  
        Color endColor = Color.white; // Ou Color.black, dependendo do efeito desejado

        float duration = 1.0f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            
            // Interpolação linear da cor
            Color currentColor = Color.Lerp(startColor, endColor, t);
            
            // Aplica a cor ao RawImage
            displayImage.color = currentColor;

            // Opcional: Se quiser parar de receber vídeo durante o fade, desvincula aqui
            // Se quiser continuar recebendo vídeo no fundo, mantenha a textura atualizada acima.
            
            yield return null;
        }

        // Finaliza a cor
        displayImage.color = endColor;
    }

    void OnDestroy()
    {
        if (webRtcUpdateCoroutine != null)
        {
            StopCoroutine(webRtcUpdateCoroutine);
            webRtcUpdateCoroutine = null;
        }
        if (peerConnection != null)
        {
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }
    }
}