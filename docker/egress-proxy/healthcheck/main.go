package main

import (
	"fmt"
	"net"
	"os"
	"time"
)

func main() {
	connection, err := net.DialTimeout("tcp", "127.0.0.1:4750", 2*time.Second)
	if err != nil {
		fmt.Fprintf(os.Stderr, "smokescreen listener is unavailable: %v\n", err)
		os.Exit(1)
	}

	if err := connection.Close(); err != nil {
		fmt.Fprintf(os.Stderr, "smokescreen healthcheck close failed: %v\n", err)
		os.Exit(1)
	}
}
