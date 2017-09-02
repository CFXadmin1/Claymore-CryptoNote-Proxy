#!/usr/bin/python2.7

import os
import sys
import socket
import threading
import json
import string
import binascii
import time

from datetime import datetime
from collections import OrderedDict

is_windows = 1 if os.name=='nt' else 0
if is_windows:
    from ctypes import windll, Structure, c_short, c_ushort, byref

# A list with the DevFee ports used to identify the shares
global devfee_ports
devfee_ports = []

# List with shares count
global total_shares
total_shares = [0,0,0] # Normal, DevFee, Rejected

# winbase.h
STD_INPUT_HANDLE = -10
STD_OUTPUT_HANDLE = -11
STD_ERROR_HANDLE = -12

# wincon.h
FOREGROUND_BLACK     = ['\033[30m', 0x0000]
FOREGROUND_BLUE      = ['\033[94m', 0x0001]
FOREGROUND_GREEN     = ['\033[92m', 0x0002]
FOREGROUND_CYAN      = ['\033[96m', 0x0003]
FOREGROUND_RED       = ['\033[91m', 0x0004]
FOREGROUND_MAGENTA   = ['\033[94m', 0x0005]
FOREGROUND_YELLOW    = ['\033[93m', 0x0006]
FOREGROUND_GREY      = ['\033[97m', 0x0007]
FOREGROUND_INTENSITY = 0x0008


def get_text_attr():
    """Return the character attributes (colors) of the console screen
    buffer."""

    if is_windows:
        SHORT = c_short
        WORD = c_ushort

        class COORD(Structure):
            """struct in wincon.h."""
            _fields_ = [
                ("X", SHORT),
                ("Y", SHORT)]

        class SMALL_RECT(Structure):
            """struct in wincon.h."""
            _fields_ = [
                ("Left", SHORT),
                ("Top", SHORT),
                ("Right", SHORT),
                ("Bottom", SHORT)]

        class CONSOLE_SCREEN_BUFFER_INFO(Structure):
            """struct in wincon.h."""
            _fields_ = [
                ("dwSize", COORD),
                ("dwCursorPosition", COORD),
                ("wAttributes", WORD),
                ("srWindow", SMALL_RECT),
                ("dwMaximumWindowSize", COORD)]

        csbi = CONSOLE_SCREEN_BUFFER_INFO()
        windll.kernel32.GetConsoleScreenBufferInfo(windll.kernel32.GetStdHandle(STD_OUTPUT_HANDLE), byref(csbi))
        return csbi.wAttributes

    else:
        return '\033[0m\n'

def set_text_attr(color):
    """Set the character attributes (colors) of the console screen
    buffer. Color is a combination of foreground and background color,
    foreground and background intensity."""
    if is_windows:
        windll.kernel32.SetConsoleTextAttribute(windll.kernel32.GetStdHandle(STD_OUTPUT_HANDLE), color)
    else:
        print color,

default_colors = get_text_attr()

def print_color(args, color):
    set_text_attr(color)
    print(args)
    set_text_attr(default_colors)

def print_green(args): print_color(args, FOREGROUND_GREEN[is_windows])
def print_red(args): print_color(args, FOREGROUND_RED[is_windows])
def print_cyan(args): print_color(args, FOREGROUND_CYAN[is_windows])
def print_yellow(args): print_color(args, FOREGROUND_YELLOW[is_windows])

class _Getch:
    """Gets a single character from standard input.  Does not echo to the screen."""
    def __init__(self):
        if is_windows:
            self.impl = _GetchWindows()
        else:
            self.impl = _GetchUnix()

    def __call__(self): return self.impl()

class _GetchUnix:
    def __init__(self):
        import tty, sys

    def __call__(self):
        import sys, tty, termios
        fd = sys.stdin.fileno()
        old_settings = termios.tcgetattr(fd)
        try:
            tty.setraw(sys.stdin.fileno())
            ch = sys.stdin.read(1)
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)
        return ch

class _GetchWindows:
    def __init__(self):
        import msvcrt

    def __call__(self):
        import msvcrt
        return msvcrt.getch()

getch = _Getch()

def get_now():
    """Return the current time."""
    return datetime.now().strftime('%d/%m/%y %H:%M:%S')

def print_dev(args, is_dev):
    if is_dev:
        print_yellow('{} - {}'.format(get_now(),args))
    else:
        print_green('{} - {}'.format(get_now(),args))

def is_devfee(port):
    """Return True if the port is a DevFee port."""
    return True if port in devfee_ports else False

def server_loop(local_host, local_port, remote_host, remote_port):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

    try:
        print('Initializing socket...')
        server.bind((local_host, local_port))
    except:
        print_red('Failed to listen on {}:{}'.format(local_host, local_port))
        print_red('  Check for other listening sockets or correct permissions')
        sys.exit(0)

    # Listen to a maximum of (usually) 5 connections
    server.listen(5)

    # Start keyboard input thread
    kbd_thread = threading.Thread(target=key_listener)
    kbd_thread.daemon = False
    kbd_thread.start()

    print('Waiting for connections...')
    while True:
        client_socket, addr = server.accept()

        print('New connection received from {}:{}'.format(addr[0], addr[1]))

        # Start a new thread to talk to the remote pool
        proxy_thread = threading.Thread(target=proxy_handler, args=(client_socket, remote_host, remote_port, addr))
        proxy_thread.daemon = False # Python program can exit shutting down the thread abruptly
        proxy_thread.start()

        
def key_listener():
    """Read keyboard input."""
    while True:
        char = getch()
        if 's' in char:
            print_cyan('Total Shares: {}, Normal: {}, DevFee: {}, Rejected: {}'
                .format(sum(total_shares), total_shares[0], total_shares[1], total_shares[2]))

        time.sleep(0.1) # Reduce CPU usage

def receive_from(connection):
    """Receive the buffer string."""
    buffer = ''

    # Blocking mode
    connection.settimeout(0)

    try:
        while True:
            data = connection.recv(4096)
            if not data:
                break
            buffer += data
    except:
        pass

    return buffer


def request_handler(socket_buffer, port):
    """Modify the request."""
    if 'login' in socket_buffer:
        json_data = json.loads(socket_buffer, object_pairs_hook=OrderedDict)
        if ('login' in json_data['method']):
            print('Login with wallet: ' + json_data['params']['login'])
            if wallet not in json_data['params']['login']:
                print_yellow(get_now() + ' - DevFee detected')
                print_yellow('  DevFee Wallet: ' + json_data['params']['login'])

                # Replace wallet
                json_data['params']['login'] = wallet + worker_name
                print_cyan('  New Wallet: ' + json_data['params']['login'])

                # Add to DevFee ports list
                devfee_ports.append(port)

                # Serialize new JSON
                socket_buffer = json.dumps(json_data) + '\n'

    return socket_buffer


def proxy_handler(client_socket, remote_host, remote_port, receive_addr):
    """A thread to parse and modify the packets received and sent in a particular port."""
    
    local_host = receive_addr[0]
    local_port = receive_addr[1]
    
    remote_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)

    # Try to connect to the remote pool
    for attempt_pool in range(3):
        try:
            remote_socket.connect((remote_host, remote_port))
        except:
            print_red('{} - Connection lost with <{}:{}>. Retry: {}/3'.format(get_now(), remote_host, remote_port, attempt_pool))
            time.sleep(1)
        else:
            # Connection OK
            break
    else:
        print_red(get_now() + ' - Could not connect to the pool.')

        # Close connection
        client_socket.shutdown(socket.SHUT_RDWR)
        client_socket.close()

        # Exit thread
        sys.exit()

    # Main loop
    while True:
        # Read packet from the local host
        local_buffer = receive_from(client_socket)

        if len(local_buffer):
            # Modify the local packet
            local_buffer = request_handler(local_buffer, local_port)

            # Send the modified packet to the remote pool
            try:
                remote_socket.send(local_buffer)
            except socket.error as se:
                print_red(get_now() + ' - Packed send to pool failed')
                print_red('  Socket error({}): {}'.format(se.errno, se.strerror))

                print_red('  Packet lost for {}: {}'.format(local_port, local_buffer))
                print_red(get_now() + ' - Connection with pool lost. Claymore should reconnect...')

                # Close connection
                client_socket.shutdown(socket.SHUT_RDWR)
                client_socket.close()

                break # Main loop


        # Read packet from the remote pool
        remote_buffer = receive_from(remote_socket)

        if len(remote_buffer):
            try:
                # Send the response to the local host
                client_socket.send(remote_buffer)

                # Some logging after send
                if 'status' in remote_buffer:
                    json_data = json.loads(remote_buffer, object_pairs_hook=OrderedDict)
                    if json_data['id'] == 4:
                        if 'OK' in json_data['result']['status']:
                            print_dev('Share submitted! ({})'.format(local_port), is_devfee(local_port))
                            total_shares[1 if is_devfee(local_port) else 0] += 1
                        else:
                            print_red('{} - Share rejected! ({})'.format(get_now(), local_port))
                            total_shares[2] += 1
                            print(remote_buffer)
                    elif json_data['id'] == 1:
                        if 'OK' in json_data['result']['status']:
                            print_dev('Stratum - Connected ({})'.format(local_port), is_devfee(local_port))
                    else:
                        print(remote_buffer)
            except:
                print_red('{} - Disconnected! ({}) Mining stopped?'.format(get_now(), local_port))
                client_socket.close()
                break

        time.sleep(0.001) # Reduce CPU usage

    # Delete this port from DevFee ports list
    if is_devfee(local_port):
        devfee_ports.remove(local_port)

    # Exit thread
    sys.exit()


def main():
    if len(sys.argv[1:]) < 3:
        print 'Arguments: local_host:port remote_pool:port wallet [worker]'
        print 'Example: 127.0.0.1:14001 xmr-us-east1.nanopool.org:14444 WALLET.PAYMENTID my_worker/my@mail'
        sys.exit(0)

    # Header
    print(string.replace(u'\n\u2554-----------------------------------------------------------------\u2557', '-', u'\u2550'))
    print(u'\u2551                    Claymore XMR Proxy  v1.0                     \u2551')
    print(string.replace(u'\u255A-----------------------------------------------------------------\u255D', '-', u'\u2550'))

    # Local host
    local_host = sys.argv[1].split(':')[0]
    local_port = int(sys.argv[1].split(':')[1])
    print_cyan('Local host is ' + sys.argv[1])

    # Remote host (pool)
    remote_host = sys.argv[2].split(':')[0]
    remote_port = int(sys.argv[2].split(':')[1])
    print_cyan('Main pool is ' + sys.argv[2])

    global wallet 
    wallet = sys.argv[3]

    global worker_name
    if len(sys.argv) > 4:
        worker_name = sys.argv[4]
    else:
        worker_name = ''

    # Check if there is a PaymentID in the wallet
    wallet_parts = wallet.split('.')
    print_cyan('Wallet is ' + wallet_parts[0])
    if len(wallet_parts) > 1:
        print_cyan('  PaymentID is ' + wallet_parts[1])

    if worker_name:
        print_cyan('  Worker is ' + worker_name)

    pool_slash = [] # Pools with slash. ex: address/worker
    pool_dot = ['nanopool.org'] # Pools with dot. ex: address.worker
    if worker_name:
        if any(s in remote_host for s in pool_slash):
            worker_name = '/' + worker_name
        elif any(d in remote_host for d in pool_dot):
            worker_name = '.' + worker_name
        else:
            print_red('Unknown pool - Ignoring worker name')

    print('Based on https://github.com/JuicyPasta/Claymore-No-Fee-Proxy by JuicyPasta & Drdada')
    print('As Claymore v9.7 beta, the DevFee logins at the start and takes some hashes all the time, '
          'like 1 hash every 39 of yours (there is not connection/disconections for devfee like in ETH). '
          'This proxy tries to replace the wallet in every login detected that is not your wallet.\n')
    print_yellow('Indentified DevFee shares are printed in yellow')
    print_green('Your shares are printed in green')
    print('Press "s" for current statistics\n')

    # Listening loop
    server_loop(local_host, local_port, remote_host, remote_port)


if __name__ == "__main__":
    main()
