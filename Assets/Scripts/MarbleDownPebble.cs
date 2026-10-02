using UnityEngine;

// Hands a stone's collisions back to MarbleDown. Unity only sends the callback to
// the GameObject that owns the Rigidbody2D, and MarbleDown builds every stone at
// runtime, so this is all it is: a forwarder with nothing to configure.
[DisallowMultipleComponent]
public class MarbleDownPebble : MonoBehaviour
{
    public System.Action<Collision2D> hit;

    void OnCollisionEnter2D(Collision2D collision) => hit?.Invoke(collision);
}
