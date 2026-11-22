using PcMaintenance.Remote.Core.Crypto;
using PcMaintenance.Remote.Core.Protocol;

namespace PcMaintenance.Remote.Agent.Session;

/// <summary>
/// 被控端的同意決策，實作配對與隱藏模式：
/// - 要求隱藏，且對方憑證有效、身分簽章對、該身分已配對 → 直接同意、隱藏進行，<b>完全不觸發任何提示</b>。
/// - 其餘情況走正常路徑：跳出提示請人同意（<see cref="VisiblePrompt"/>）；同意後若對方身分有效就完成配對，
///   下次該管理者才可進隱藏模式。未配對時忽略隱藏旗標，一律正常顯示。
/// 身分有效＝憑證由公司根金鑰簽發、未過期、授權此模式與本機群組，且附帶的身分簽章能用憑證內身分公鑰驗過
/// （證明真的握有身分私鑰，光側錄一張憑證無法冒用）。
/// </summary>
public sealed class PairingConsentPolicy(
    CertificateVerifier verifier,
    PairingStore store,
    string[] agentGroups)
{
    /// <summary>正常模式要人同意時呼叫；回傳 true 代表同意。隱藏模式不會呼叫到這裡。預設一律同意（主控台用）。</summary>
    public Func<SessionRequestMessage, bool> VisiblePrompt { get; set; } = _ => true;

    public ConsentDecision Decide(SessionRequestMessage req)
    {
        string? fingerprint = ValidatedIdentityFingerprint(req);

        // 已配對的管理者要求隱藏 → 靜默同意，不跳任何提示、不留痕跡
        if (req.RequestHidden && fingerprint is not null && store.IsPaired(fingerprint))
            return new ConsentDecision { Accept = true, Hidden = true };

        // 正常路徑：請人同意
        if (!VisiblePrompt(req))
            return ConsentDecision.Reject("使用者拒絕連線");

        // 同意後，若對方身分有效就完成配對，下次可進隱藏模式
        if (fingerprint is not null)
        {
            store.Pair(fingerprint, CertName(req));
            return new ConsentDecision { Accept = true, Hidden = false, PairingEstablished = true };
        }
        return ConsentDecision.AcceptVisible;
    }

    /// <summary>驗證憑證與身分簽章；全部通過才回傳指紋，否則 null（當作匿名、只能走正常同意、不能配對）。</summary>
    private string? ValidatedIdentityFingerprint(SessionRequestMessage req)
    {
        if (req.Certificate is not { } signed || req.IdentitySignature is not { } sig) return null;

        // 群組：憑證要授權本機任一群組（這裡自行檢查，故 Verify 的群組參數傳 null）
        bool groupOk = agentGroups.Length == 0 || Array.Exists(agentGroups, g => signed.Certificate.AllowsGroup(g));
        if (!groupOk) return null;

        var check = verifier.Verify(signed, req.Mode, requiredGroup: null, DateTimeOffset.UtcNow);
        if (!check.IsValid) return null;

        if (!SessionBinding.Verify(signed.Certificate.AdminPublicKey, req.RequestId, req.ControllerPublicKey, sig))
            return null;

        return SessionBinding.Fingerprint(signed.Certificate.AdminPublicKey);
    }

    private static string CertName(SessionRequestMessage req) =>
        req.Certificate?.Certificate.AdminName ?? req.AdminName;
}
