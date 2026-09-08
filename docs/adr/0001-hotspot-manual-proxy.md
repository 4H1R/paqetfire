# Hotspot manual proxy over transparent ICS routing

We expose hotspot clients through a manual authenticated SOCKS listener on the hotspot IP reusing the Xray route, because transparently forcing Windows ICS NAT through Paqet requires WFP/NAT interception, fights ICS itself, and risks silent direct leaks.
