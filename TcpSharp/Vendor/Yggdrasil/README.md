# Yggdrasil TCP subset

From https://github.com/exectails/Yggdrasil commit b79465e0b32773df3dd7923681efe9af5713fac4 (the repository commit recorded by ExecTails.Yggdrasil 2.0.11). MIT license included. Only TCP connection, acceptor, and connection-check source files are included; compiled into TcpSharp.

Local fixes: stop closes the listening socket without creating an unbound replacement; accept callbacks use their original socket and stop retrying after shutdown; close events are registered before accepting consumers; sends retain offsets across partial socket writes; remote EOF closes the socket. Unused namespace imports removed and nullable checking disabled in upstream files.

The package acceptor shutdown produced SocketException operation-aborted followed by an unhandled Bind-required exception in the host smoke check. Vendoring this small subset makes the fixes reviewable without private-field reflection or a silent dependency patch.
