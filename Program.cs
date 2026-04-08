using System;
public class Host {
    public event Action? MyEvent;
}
public class Program {
    public static void Main() {
        Host? h = new Host();
        h?.MyEvent += () => {};
    }
}
