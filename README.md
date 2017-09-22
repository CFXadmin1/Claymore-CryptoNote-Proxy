# Claymore CryptoNote No DevFee Proxy
Removes Claymore's 2%-2.5% mining developer fee using Stratum Proxy. Tested on Windows 10 Pro x64 with **_Claymore CryptoNote GPU Miner v9.7 Beta_**.

## How it works?
This proxy is placed between Claymore and Internet in order to catch the mining fee login packet and replacing the DevFee address with your wallet address.

## Requirements
#### Windows executable
* .Net Framework 4.0 or better

## Proxy.txt configuration file
```json
{
    "local_host": "xmr.mycustompool.org:14001",
    "pool_address": "xmr-us-east1.nanopool.org:14433",
    "wallet": "MYWALLET",
    "worker": "little_worker",
    "ssl": true
}
```
- **local_host**: IP/host name and Port to use as the proxy/server. Do **NOT** use ```127.0.0.1``` or ```localhost```, see [Adding a custom IP and Hostname in the Wiki](https://github.com/NanMetal/Claymore-CryptoNote-Proxy/wiki/Add-a-custom-IP-and-host-name).
- **pool_address**: The pool address.
- **wallet**: Your Monero or exchange wallet with the Base and PaymentID.
- **worker**: Worker name for the DevFee. Can be ```null```.
- **ssl**: ```true``` to use SSL/TLS connection to the pool, or ```false```.

## Setup
### Configure a custom IP and host name in Windows
See [Adding a custom IP and Hostname in the Wiki](https://github.com/NanMetal/Claymore-CryptoNote-Proxy/wiki/Add-a-custom-IP-and-host-name).

### Configure Claymore
Edit your ```.bat``` or ```config.txt``` file with the ```local_host``` IP/host name used in the proxy:
```batch
-o stratum+tcp://xmr.mycustompool.org:14001 ...
```

## Run
Start ```stratum_proxy.exe``` and Claymore software.

## Features
* Redirect DevFee shares to your wallet
* Support SSL/TLS (only pool connection)
* Shows shares accepted, rejected and DevFee
* Custom worker name for DevFee
* Low CPU and memory usage
* Detect when a miner connect and disconnects
* Colorful output
* Double click to start (no command arguments)

## FAQ

### What is the connection flow of SSL/TLS?
Claymore Software (**NOT using SSL/TLS**) → Stratum Proxy (using SSL/TLS) → Remote Pool (using SSL/TLS)

### Why don't use Claymore with SSL/TLS?
How SSL/TLS works, I can't decode the request nor the response because I don't have the private key for that (the pool server does). A workaround would be create a fake certificate and use it for local connections, decode the local messages then encode them with public key from the pool. For the moment I will not do it (probably in future releases). So, don't use Claymore with SSL/TLS.

### What if I use other pool?
Claymore tries to mine the devfee on the same pool as you. You can try using  "-allpools 1" if it is not working. If not, you're out of luck, as the connection made from Claymore is bypassing the proxy.

### Is it compatible with every currency?
This proxy was designed to be used with Claymore CryptoNote version mining Monero (XMR). I did not test others CryptoNight-like currencies.

### How can I check if it works?
You can check your pool stats, but some pool ignore small mining time if it did not find a share. But it mines for you!

![devfee_shares](https://user-images.githubusercontent.com/6496385/29857323-86394312-8d2e-11e7-9ffa-83ad8399b747.png)

### Claymore warns me something about local proxy...
Claymore checks the pool's IP to avoid local proxies, if you have the warning make sure you are not using ```localhost``` or ```127.0.0.1```.  See [Adding a custom IP and Hostname in the Wiki](https://github.com/NanMetal/Claymore-CryptoNote-Proxy/wiki/Add-a-custom-IP-and-host-name).

### This works with the Claymore CPU version?
Yes but it's slow. Use [XMR-Stak-CPU - Monero mining software](https://github.com/fireice-uk/xmr-stak-cpu), is alot better.

### Why is the application using 127.0.0.1 in the default configuration?
It's there only for testing. Don't forget to change it to a custom host name.

## License
This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details

## Credit & Donations
You can send a donation (ETH) to the authors of the original script:
- [JuicyPasta](https://github.com/JuicyPasta) - 0xfeE03fB214Dc0EeDc925687D3DC9cdaa1260e7EF (Ethereum)
- [Drdada](https://github.com/drdada) - 0xB7716d5A768Bc0d5bc5c216cF2d85023a697D04D (ethermine)

And if you want, not necessary:
- XMR: 
```48Ba1QniibJ34Di57C96M58Gq5EQ7ixuUNxcwnfe8zeCgy8UjCZWf4NAQW5P3iuE8EYc3MkA7DQ9USWDmijbN7b9HPZVTb8```
- BTC: ```3DTo7Nkkg64YhpdA9NnmV3TuDiQ8WcufrU```

