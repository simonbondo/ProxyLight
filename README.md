Trying to make the smallest possible docker image for a .NET app.

Build using this command from src folder:
```sh
docker build -t proxy . && docker run -t --rm -p 5000:5000 -p 8080:8080 --name proxy proxy:latest
```

`ldd` shows dynamically linked libraries, i think.
```sh
I have no name!@18b998df8ae7:/app$ ldd ProxyLight
        linux-vdso.so.1 (0x00007fffbd9fe000)
        libm.so.6 => /lib/x86_64-linux-gnu/libm.so.6 (0x000075f4c022a000)
        libc.so.6 => /lib/x86_64-linux-gnu/libc.so.6 (0x000075f4c0049000)
        /lib64/ld-linux-x86-64.so.2 (0x000075f4c166b000)
```
