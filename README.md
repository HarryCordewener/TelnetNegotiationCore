# Telnet Negotiation Core
## Summary
This project is intended to be a library that implements basic telnet functionality. 
This is done with an eye on MUDs at this time, but may improve to support more terminal capabilities as time permits.

At this time, this repository is in a rough state and does not yet implement some common modern code standards. 

## Support
| RFC                                         | Description                        | Supported  | Comments           |
| ------------------------------------------- | ---------------------------------- |------------| ------------------ |
| http://www.faqs.org/rfcs/rfc855.html        | Telnet Option Specification        | Full       |                    |
| http://www.faqs.org/rfcs/rfc858.html        | Suppress GOAHEAD Negotiation       | Full       | Untested           |
| http://www.faqs.org/rfcs/rfc1091.html       | Terminal Type Negotiation          | Full       |                    |
| https://tintin.mudhalla.net/protocols/mtts  | MTTS Negotiation (Extends TTYPE)   | Full       |                    |
| http://www.faqs.org/rfcs/rfc885.html        | End Of Record Negotiation          | Full       | Untested           | 
| https://tintin.mudhalla.net/protocols/eor   | End Of Record Negotiation          | Full       | Untested           |
| http://www.faqs.org/rfcs/rfc1073.html       | Window Size Negotiation (NAWS)     | Full       |                    |
| http://www.faqs.org/rfcs/rfc2066.html       | Charset Negotiation                | Partial    | No TTABLE support  |
| https://tintin.mudhalla.net/protocols/mssp  | MSSP Negotiation (Extents 855)     | Serverside | Planned & Untested |
| http://www.faqs.org/rfcs/rfc1572.html       | New Environment Negotiation        | No         | Planned            |
| https://tintin.mudhalla.net/protocols/mnes  | Mud New Environment Negotiation    | No         | Planned            |
| https://tintin.mudhalla.net/protocols/msdp  | Mud Server Data Protocol           | No         | Planned            |
| https://tintin.mudhalla.net/rfc/rfc1950/    | ZLIB Compression                   | No         | Planned            |
| https://tintin.mudhalla.net/protocols/mccp  | Mud Client Compression Protocol 	 | No         | Planned            |
| https://tintin.mudhalla.net/protocols/gmcp  | Generic Mud Communication Protocol | No         | Planned            |
| https://tintin.mudhalla.net/protocols/mccp  | Mud Client Compression Protocol	   | No         | Planned            |
| http://www.faqs.org/rfcs/rfc857.html        | Echo Negotiation                   | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1079.html       | Terminal Speed Negotiation         | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1372.html       | Flow Control Negotiation           | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1184.html       | Line Mode Negotiation              | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1096.html       | X-Display Negotiation              | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc1408.html       | Environment Negotiation            | No         | Rejects            | 
| http://www.faqs.org/rfcs/rfc2941.html       | Authentication Negotiation         | No         | Rejects            |
| http://www.faqs.org/rfcs/rfc2946.html       | Encryption Negotiation             | No         | Rejects            |

## ANSI Support, ETC?
Being a Telnet Negotiation Library, this library doesn't give support for extensions like ANSI, Pueblo, MXP, etc at this time.

# Todo
* Create a Changelog
* Use Github Actions to create Nuget Package