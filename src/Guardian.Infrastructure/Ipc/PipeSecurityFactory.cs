using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Guardian.Infrastructure.Ipc;

/// <summary>
/// 管道安全描述符：
/// - SYSTEM 与 Administrators：完全控制（服务自身与管理员工具）；
/// - Users：读写（本机登录用户可连接）；
/// - 敏感命令的授权在连接建立后由 CallerVerifier 在带内二次校验。
/// </summary>
public static class PipeSecurityFactory
{
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return security;
    }

    public static NamedPipeServerStream CreateServerStream(string pipeName)
        => NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            0,
            0,
            PipeSecurityFactory.Create());
}
