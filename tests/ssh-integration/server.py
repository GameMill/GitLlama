"""Disposable loopback-only Git-over-SSH server for manual acceptance. Requires Paramiko."""
import socket
import subprocess
import sys
import threading
import time
import paramiko

repo = sys.argv[1]
key = paramiko.RSAKey.generate(2048)

class Server(paramiko.ServerInterface):
    def __init__(self):
        self.command = None
        self.ready = threading.Event()
    def check_auth_publickey(self, username, public_key):
        return paramiko.AUTH_SUCCESSFUL if username == "git" else paramiko.AUTH_FAILED
    def get_allowed_auths(self, username):
        return "publickey"
    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_exec_request(self, channel, command):
        self.command = command.decode("utf-8")
        self.ready.set()
        return True

def handle(connection):
    transport = paramiko.Transport(connection)
    try:
        transport.add_server_key(key)
        server = Server()
        transport.start_server(server=server)
        channel = transport.accept(20)
        if channel is None or not server.ready.wait(20):
            return
        operation = server.command.split(" ", 1)[0]
        if operation not in ("git-upload-pack", "git-receive-pack"):
            channel.send_exit_status(1)
            return
        process = subprocess.Popen(["git", operation[4:], repo], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        def upload():
            try:
                while True:
                    data = channel.recv(65536)
                    if not data:
                        break
                    process.stdin.write(data)
                    process.stdin.flush()
            except (OSError, EOFError):
                pass
            finally:
                process.stdin.close()
        def download(stream, send):
            try:
                while True:
                    data = stream.read1(65536)
                    if not data:
                        break
                    send(data)
            except (OSError, EOFError):
                pass
        threading.Thread(target=upload, daemon=True).start()
        errors = threading.Thread(target=download, args=(process.stderr, channel.send_stderr), daemon=True)
        errors.start()
        download(process.stdout, channel.sendall)
        errors.join()
        channel.send_exit_status(process.wait())
        channel.shutdown_write()
        # Let the client consume the final packets before closing the transport.
        for _ in range(50):
            if channel.closed:
                break
            time.sleep(0.1)
        channel.close()
    except (OSError, EOFError, paramiko.SSHException) as error:
        print(str(error), file=sys.stderr, flush=True)
    finally:
        transport.close()

listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen()
print(listener.getsockname()[1], flush=True)
while True:
    connection, _ = listener.accept()
    threading.Thread(target=handle, args=(connection,), daemon=True).start()
