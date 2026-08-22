# Request collection

These `.http` files are executable from Rider and from the VS Code REST Client
extension. They are kept in the repository on purpose: version-controlled
request examples double as the API documentation deliverable, and they cannot
drift out of the repo the way an exported Postman collection does.

One file per service, added as each service is built.

`_env.http` holds the shared variables. Point `@host` at the gateway once the
gateway exists; until then each file targets its service directly.
