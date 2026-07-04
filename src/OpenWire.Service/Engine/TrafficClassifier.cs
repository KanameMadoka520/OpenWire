namespace OpenWire.Service.Engine;

/// <summary>Maps a remote port to a human-readable protocol / traffic-type name
/// (GlassWire-style), used for the Usage "traffic types" breakdown.</summary>
public static class TrafficClassifier
{
    public static string Classify(int port) => port switch
    {
        443 or 8443 => "Hypertext Transfer Protocol over TLS/SSL (HTTPS)",
        80 or 8080 or 8000 => "Hypertext Transfer Protocol (HTTP)",
        53 => "Domain Name System (DNS)",
        5353 => "Multicast DNS (mDNS)",
        5355 => "Link-Local Multicast Name Resolution (LLMNR)",
        22 => "Secure Shell (SSH)",
        23 => "Telnet",
        21 or 20 => "File Transfer Protocol (FTP)",
        25 or 465 or 587 => "Simple Mail Transfer Protocol (SMTP)",
        110 or 995 => "Post Office Protocol (POP3)",
        143 or 993 => "Internet Message Access Protocol (IMAP)",
        3389 => "Remote Desktop Protocol (RDP)",
        3390 => "Intel Remote Desktop Management",
        123 => "Network Time Protocol (NTP)",
        137 or 138 or 139 => "NetBIOS",
        445 => "Server Message Block (SMB)",
        67 or 68 => "Dynamic Host Configuration Protocol (DHCP)",
        1900 => "Simple Service Discovery Protocol (SSDP)",
        5060 or 5061 => "Session Initiation Protocol (SIP)",
        1935 => "Real-Time Messaging Protocol (RTMP)",
        3478 or 3479 => "STUN / TURN",
        161 or 162 => "Simple Network Management Protocol (SNMP)",
        389 or 636 => "Lightweight Directory Access Protocol (LDAP)",
        1433 => "Microsoft SQL Server",
        3306 => "MySQL",
        5432 => "PostgreSQL",
        6379 => "Redis",
        27017 => "MongoDB",
        1883 or 8883 => "MQTT",
        _ => "Other",
    };
}
