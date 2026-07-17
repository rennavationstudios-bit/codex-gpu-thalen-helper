using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ThalenHelper.Core;

internal static class LmStudioPeerVerifier
{
    private const uint ErrorInsufficientBuffer = 122;
    private const int AfInet = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const uint WaitTimeout = 0x00000102;
    private const uint MibTcpStateEstablished = 5;
    private const int TcpTableOwnerPidConnections = 4;
    private const int TokenUserInformationClass = 1;
    private static readonly TimeSpan OwnershipLookupTimeout = TimeSpan.FromMilliseconds(500);

    public static async ValueTask<Stream> ConnectVerifiedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var address)
            || !IPAddress.IsLoopback(address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new LmStudioPeerIdentityException("The LM Studio connection target was not IPv4 loopback.");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        SafeProcessHandle? processHandle = null;
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port),
                cancellationToken).ConfigureAwait(false);
            var processId = await FindServerProcessIdAsync(socket, cancellationToken).ConfigureAwait(false)
                ?? throw new LmStudioPeerIdentityException(
                    "The process that owns the LM Studio TCP connection could not be identified.");
            processHandle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, processId);
            if (processHandle.IsInvalid)
            {
                throw new LmStudioPeerIdentityException(
                    "The process that owns the LM Studio TCP connection could not be opened safely.");
            }

            var executablePath = QueryProcessImagePath(processHandle);
            var processSid = QueryProcessUserSid(processHandle);
            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentSid = currentIdentity.User?.Value;
            var signatureValid = AuthenticodeVerifier.Verify(executablePath, "Element Labs Inc.");
            if (!IsTrustedIdentity(executablePath, processSid, currentSid, signatureValid)
                || WaitForSingleObject(processHandle, 0) != WaitTimeout)
            {
                throw new LmStudioPeerIdentityException(
                    "The loopback endpoint is not owned by the expected current-user LM Studio process.");
            }

            var verifiedStream = new VerifiedPeerNetworkStream(socket, processHandle);
            processHandle = null;
            return verifiedStream;
        }
        catch
        {
            processHandle?.Dispose();
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsTrustedIdentity(
        string executablePath,
        string? processSid,
        string? currentSid,
        bool signatureValid)
    {
        if (!signatureValid
            || string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || string.IsNullOrWhiteSpace(processSid)
            || string.IsNullOrWhiteSpace(currentSid)
            || !string.Equals(processSid, currentSid, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileName(executablePath),
            "LM Studio.exe",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<uint?> FindServerProcessIdAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + OwnershipLookupTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processId = FindServerProcessId(socket);
            if (processId is not null)
            {
                return processId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline);

        return null;
    }

    private static uint? FindServerProcessId(Socket socket)
    {
        if (socket.LocalEndPoint is not IPEndPoint client
            || socket.RemoteEndPoint is not IPEndPoint server
            || client.AddressFamily != AddressFamily.InterNetwork
            || server.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var size = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            false,
            AfInet,
            TcpTableOwnerPidConnections,
            0);
        if (result != ErrorInsufficientBuffer || size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                false,
                AfInet,
                TcpTableOwnerPidConnections,
                0);
            if (result != 0)
            {
                return null;
            }

            var count = checked((int)(uint)Marshal.ReadInt32(buffer));
            var firstRowOffset = Marshal.OffsetOf<MibTcpTableOwnerPidHeader>(
                nameof(MibTcpTableOwnerPidHeader.FirstRow)).ToInt32();
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var expectedLocalAddress = ToNativeAddress(server.Address);
            var expectedRemoteAddress = ToNativeAddress(client.Address);
            for (var index = 0; index < count; index++)
            {
                var rowPointer = IntPtr.Add(buffer, checked(firstRowOffset + (index * rowSize)));
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                if (row.State == MibTcpStateEstablished
                    && row.LocalAddress == expectedLocalAddress
                    && ToHostPort(row.LocalPort) == server.Port
                    && row.RemoteAddress == expectedRemoteAddress
                    && ToHostPort(row.RemotePort) == client.Port)
                {
                    return row.OwningProcessId;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ToNativeAddress(IPAddress address)
        => BitConverter.ToUInt32(address.GetAddressBytes(), 0);

    private static int ToHostPort(uint nativePort)
        => unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)(nativePort & ushort.MaxValue))));

    private static string QueryProcessImagePath(SafeProcessHandle processHandle)
    {
        var buffer = new StringBuilder(32_768);
        var length = buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref length) || length <= 0)
        {
            throw new LmStudioPeerIdentityException("The LM Studio peer executable path could not be read.");
        }

        return buffer.ToString(0, length);
    }

    private static string QueryProcessUserSid(SafeProcessHandle processHandle)
    {
        if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
        {
            throw new LmStudioPeerIdentityException("The LM Studio peer user identity could not be opened.");
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenUserInformationClass, IntPtr.Zero, 0, out var requiredLength);
            if (requiredLength <= 0)
            {
                throw new LmStudioPeerIdentityException(
                    "The LM Studio peer user identity size could not be determined.");
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                if (!GetTokenInformation(
                    tokenHandle,
                    TokenUserInformationClass,
                    buffer,
                    requiredLength,
                    out _))
                {
                    throw new LmStudioPeerIdentityException("The LM Studio peer user identity could not be read.");
                }

                var sidPointer = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sidPointer).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(tokenHandle);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpTableOwnerPidHeader
    {
        public uint EntryCount;
        public MibTcpRowOwnerPid FirstRow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    private sealed class VerifiedPeerNetworkStream(Socket socket, SafeProcessHandle processHandle)
        : NetworkStream(socket, ownsSocket: true)
    {
        private SafeProcessHandle? _processHandle = processHandle;

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    Interlocked.Exchange(ref _processHandle, null)?.Dispose();
                }
            }
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class LmStudioPeerIdentityException(string message) : IOException(message);
