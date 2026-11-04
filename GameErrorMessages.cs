using DeltaForceMode.Purchasing;

namespace DeltaForceMode;

public static class GameErrorMessages
{
    public static string FromGateway(string? code, string? fallback)
    {
        if (Enum.TryParse<GatewayErrorCode>(code, ignoreCase: true, out var gatewayCode))
        {
            return gatewayCode switch
            {
                GatewayErrorCode.NotLinked => "SSG 账户尚未绑定，请先输入 !link。",
                GatewayErrorCode.InsufficientFunds => "SSG 余额不足。",
                GatewayErrorCode.LimitExceeded => "超过 SSG 单笔、每日或总额度限制。",
                GatewayErrorCode.Revoked => "SSG 授权已撤销，请重新输入 !link。",
                GatewayErrorCode.InvalidRequest => "请求参数无效，未完成购买。",
                GatewayErrorCode.RateLimited => "请求过于频繁，请稍后再试。",
                GatewayErrorCode.Timeout => "支付结果暂不可确认，请用 !pending 查询。",
                GatewayErrorCode.Unavailable => "支付服务暂不可用，未确认购买。",
                GatewayErrorCode.Unknown => "支付结果未知，请用 !pending 查询。",
                _ => fallback ?? "操作失败。"
            };
        }

        return code switch
        {
            GatewayErrorCodes.GatewayNotConfigured => "支付接口尚未接入，请联系管理员。",
            GatewayErrorCodes.LocalItemNotFound => "商品配置已变化，请联系管理员。",
            GatewayErrorCodes.PurchaseAlreadyPending => "有一笔支付尚未确认，请先用 !pending 查询。",
            GatewayErrorCodes.PurchaseIdentityMismatch => "支付回执不一致，已禁止发货，请联系管理员。",
            "utility_limit" => fallback ?? "投掷物数量达到上限。",
            "slot_limit" => fallback ?? "该装备槽已达到上限。",
            "invalid_item" => fallback ?? "商品配置无效。",
            _ => fallback ?? "操作失败。"
        };
    }
}

