using PairSync.Rendezvous;

// PairSync rendezvous service (phase 5): presence and signaling for paired devices, optional TURN credentials.
// Configure in appsettings.json or with Rendezvous__… environment variables; see deploy/rendezvous.
var app = RendezvousHost.Build(args);
await app.RunAsync();
