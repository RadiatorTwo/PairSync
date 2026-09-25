namespace PairSync.Transport;

public class TransportException(string message, Exception? inner = null) : Exception(message, inner);
