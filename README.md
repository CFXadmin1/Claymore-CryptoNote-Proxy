# Claymore CryptoNote No DevFee Proxy
Removes Claymore's 2%-2.5% mining developer fee using Stratum Proxy. Tested on Windows 10 Pro x64 with **_Claymore CryptoNote GPU Miner v9.7 Beta_**.

## How it works?
This proxy is placed between Claymore and Internet in order to catch the mining fee login packet and replacing the DevFee address with your wallet address.

## Requirements
#### Python script version
* Python 2.7 or better
#### Windows executable version
* .Net Framework 4.0 or better

## Proxy Arguments
```
./stratum_proxy fakehostname:someport pooladdress:poolport mywallet [workername]
```
- fakehostname: The previous created fake hostname or _localhost_. i.e _xmr.mycustompool.org_
- someport: A port to use. Can be any, but to avoid conflicts use a bigger one. i.e: _14001_
- pooladdress: The original pool address. i.e _xmr-us-east1.nanopool.org_
- poolport: The original pool port. i.e _14444_
- mywallet: Your Monero or exchange wallet with the Base and PaymentID.
- workername: An optional worker name. Let's use a different one to know when we get shares from DevFee. i.e: _little_worker_

## Setup
### Configure on Windows
Add a fake hostname as Administrator in the "_C:/Windows/System32/drivers/etc/hosts_" file:
```
127.0.0.1   xmr.mycustompool.org
```

### Start script
```batch
stratum_proxy.exe xmr.mycustompool.org:14001 xmr-us-east1.nanopool.org:14444 YOUR_REAL_WALLET little_worker
```
For the python script version use:
```batch
py stratum_proxy.py ...
```

### Configure Claymore
Edit your .bat file to use the new fake hostname created above (or _localhost_) with the same port used in the proxy:

If we had:
```batch
NsGpuCNMiner.exe -o ssl://xmr-us-east1.nanopool.org:14433 ...
```
Now we should have:
```batch
NsGpuCNMiner.exe -o xmr.mycustompool.org:14001 ...
```
## Run
Start the proxy and Claymore software.

## Features
* Redirect DevFee shares to your wallet
* Shows shares accepted, rejected and DevFee
* Custom worker name for DevFee
* Low CPU and memory usage
* Detect when a miner connect and disconnects
* Colorful output

## FAQ

### What if I use other pool?
Claymore tries to mine the devfee on the same pool as you. You can try using  "-allpools 1".

### Is it compatible with every currency?
This proxy was designed to be used with Claymore CryptoNote version. I did not test others CryptoNight-like currencies.

### How can I check if it works?
You can check your pool stats, but some pool ignore small mining time if it did not find a share. But it mines for you!

Proof:

![devfee_shares](https://user-images.githubusercontent.com/6496385/29857323-86394312-8d2e-11e7-9ffa-83ad8399b747.png)

### Claymore warns me something about local proxy...
Claymore checks the pool's IP to avoid local proxies, if you have the warning make sure you are not using _localhost_. You can also try using a [Fake WAN For Windows](https://github.com/JuicyPasta/Claymore-No-Fee-Proxy/wiki/Creating-a-fake-WAN-network-(Win)) and in the hosts file replace the ip of the fake hostname (```127.0.0.1   xmr.mycustompool.org```) with the new ip (```194.12.12.2   xmr.mycustompool.org```).

### This works with the Claymore CPU version?
Yes but it's slow. Use [XMR-Stak-CPU - Monero mining software](https://github.com/fireice-uk/xmr-stak-cpu), is alot better.

## Credit & Donations
You can send a donation (ETH) to the authors of the original script:
- [JuicyPasta](https://github.com/JuicyPasta) - 0xfeE03fB214Dc0EeDc925687D3DC9cdaa1260e7EF (Ethereum)
- [Drdada](https://github.com/drdada) - 0xB7716d5A768Bc0d5bc5c216cF2d85023a697D04D (ethermine)
